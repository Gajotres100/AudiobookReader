using AudioBookReader.App.Services;
using AudioBookReader.Core.Data;
using AudioBookReader.Core.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioBookReader.App.ViewModels;

/// <summary>
/// One book on the server, before deciding to bring it here.
///
/// A page of its own rather than a tap that starts a download. What is about to be copied over a
/// phone's connection is worth seeing first: how long it is, whether it has the text as well as the
/// voice, and — the one that decides everything — whether the server keeps it as one file or forty.
/// </summary>
[QueryProperty(nameof(ItemId), "id")]
public partial class ServerBookViewModel(ServerConnection server, LibraryDatabase database) : ObservableObject
{
    public string ItemId { get; set; } = "";

    private ServerBookDetail? _detail;

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial string Author { get; set; } = "";

    [ObservableProperty]
    public partial string CoverUrl { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSeries))]
    public partial string Series { get; set; } = "";

    public bool HasSeries => Series.Length > 0;

    /// <summary>What the server has of it, spelled out rather than in glyphs.</summary>
    [ObservableProperty]
    public partial string Contents { get; set; } = "";

    [ObservableProperty]
    public partial string Length { get; set; } = "";

    [ObservableProperty]
    public partial string Chapters { get; set; } = "";

    /// <summary>Where it will be kept, said plainly before anything is copied.</summary>
    public string Destination =>
        "Sprema se u vlastiti spremnik aplikacije i odmah se pojavljuje u Knjigama.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CannotDownload))]
    public partial bool CanDownload { get; set; }

    public bool CannotDownload => !CanDownload && Obstacle.Length > 0;

    /// <summary>Why it cannot be brought over, when it cannot.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CannotDownload))]
    public partial string Obstacle { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsDownloading { get; set; }

    public bool IsIdle => !IsDownloading;

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string Status { get; set; } = "";

    public bool HasStatus => Status.Length > 0;

    /// <summary>Set once the book is here, so the page can offer to open it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInLibrary))]
    public partial int? BookId { get; set; }

    public bool IsInLibrary => BookId is not null;

    public async Task LoadAsync()
    {
        if (_detail is not null || string.IsNullOrEmpty(ItemId)) return;

        IsBusy = true;
        Status = "Čitam podatke…";

        try
        {
            _detail = await server.GetBookAsync(ItemId);

            var book = _detail.Book;

            Title = book.Title;
            Author = book.Author ?? "";
            CoverUrl = server.CoverUrl(book.Id);
            Series = book.InSeries
                ? string.IsNullOrEmpty(book.Sequence) ? book.Series! : $"{book.Series} #{book.Sequence}"
                : "";

            Contents = Describe(book);
            Length = book.DurationSeconds > 0
                ? $"Trajanje {TimeSpan.FromSeconds(book.DurationSeconds):h\\:mm\\:ss}"
                : "";

            Chapters = _detail.Chapters.Count > 0 ? $"{_detail.Chapters.Count} poglavlja" : "";

            // The one thing that decides whether this can be brought over at all, said here rather
            // than discovered by pressing a button that then refuses.
            Obstacle = _detail.AudioFiles.Count > 1
                ? $"Server drži zvuk razlomljen na {_detail.AudioFiles.Count} datoteka. Knjiga kod nas " +
                  "još može držati samo jednu, pa je ovu zasad ne mogu preuzeti."
                : _detail.AudioFiles.Count == 0 && _detail.Ebook is null
                    ? "Ova stavka na serveru nema ni zvuka ni teksta."
                    : "";

            CanDownload = Obstacle.Length == 0;

            // Already here? Then say so instead of offering to fetch it twice. Matched by title,
            // which is all the two sides share before a file has been downloaded and hashed.
            BookId = (await database.GetBooksAsync())
                .FirstOrDefault(b => string.Equals(b.Title, book.Title, StringComparison.CurrentCultureIgnoreCase))
                ?.Id;

            Status = "";
        }
        catch (Exception ex)
        {
            AppLog.Error("reading a book from the server", ex);
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Describe(ServerBook book) => (book.HasAudio, book.HasEbook) switch
    {
        (true, true) => "Audioknjiga i e-knjiga — tekst može pratiti naraciju.",
        (true, false) => "Samo audioknjiga.",
        (false, true) => "Samo e-knjiga.",
        _ => "",
    };

    private CancellationTokenSource? _downloading;

    [RelayCommand]
    private void Cancel()
    {
        _downloading?.Cancel();
        Status = "Prekidam preuzimanje…";
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (_detail is null || !CanDownload || IsDownloading) return;

        using var downloading = new CancellationTokenSource();

        _downloading = downloading;
        IsDownloading = true;
        Progress = 0;

        try
        {
            var progress = new Progress<ImportProgress>(p =>
            {
                Status = p.Message;
                Progress = p.Fraction;
            });

            BookId = await server.ImportAsync(_detail.Book, progress, downloading.Token);

            Status = "Knjiga je u Knjigama.";
        }
        catch (OperationCanceledException)
        {
            // Whatever was written is deleted by the code that was writing it, whichever way this
            // ended, so there is nothing half-finished left behind.
            Status = "Preuzimanje je prekinuto.";
        }
        catch (Exception ex)
        {
            AppLog.Error("downloading a book from the server", ex);
            Status = ex.Message;
        }
        finally
        {
            _downloading = null;
            IsDownloading = false;
            Progress = 0;
        }
    }

    [RelayCommand]
    private Task OpenAsync() =>
        BookId is { } id ? Shell.Current.GoToAsync($"book?id={id}") : Task.CompletedTask;

    [RelayCommand]
    private Task CloseAsync() => Shell.Current.GoToAsync("..");
}

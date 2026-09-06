using System.Collections.ObjectModel;
using AudioBookReader.App.Services;
using AudioBookReader.Core.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioBookReader.App.ViewModels;

/// <summary>One book on the server, as the shelf shows it.</summary>
public class ServerBookRow(ServerBook book, string coverUrl)
{
    public ServerBook Book { get; } = book;

    public string Title { get; } = book.Title;

    public string Author { get; } = book.Author ?? "";

    public string CoverUrl { get; } = coverUrl;

    /// <summary>Its number in the series, shown beside the row rather than folded into the title.</summary>
    public string Number { get; } = string.IsNullOrEmpty(book.Sequence) ? "" : $"#{book.Sequence}";

    public bool IsNumbered { get; } = !string.IsNullOrEmpty(book.Sequence);

    /// <summary>What the server has of it, in the same shorthand the library uses.</summary>
    public string Media { get; } = (book.HasAudio, book.HasEbook) switch
    {
        (true, true) => "🎧 📖",
        (true, false) => "🎧",
        (false, true) => "📖",
        _ => "",
    };

    /// <summary>
    /// Said plainly on the row rather than discovered on tapping. A book split across files is the
    /// commonest shape on a server and the one thing here that cannot be brought over yet.
    /// </summary>
    public string Note { get; } = book.AudioFileCount > 1
        ? $"{book.AudioFileCount} datoteka — još ne mogu složiti u jednu knjigu"
        : book.DurationSeconds > 0
            ? $"{TimeSpan.FromSeconds(book.DurationSeconds):h\\:mm}"
            : "";

    public bool CanImport { get; } = book.AudioFileCount <= 1 && (book.HasAudio || book.HasEbook);
}

/// <summary>
/// A shelf: either a series in its own order, or the books that belong to no series.
///
/// A list rather than a wrapper around one, because that is what a grouped CollectionView binds to.
/// </summary>
public class Shelf(string name, IReadOnlyList<ServerBookRow> books) : List<ServerBookRow>(books)
{
    public string Name { get; } = name;

    public string Summary { get; } = books.Count == 1 ? "1 knjiga" : $"{books.Count} knjiga";
}

public partial class ServerViewModel(ServerConnection server) : ObservableObject
{
    /// <summary>Books grouped into series, with the unaffiliated ones last.</summary>
    public ObservableCollection<Shelf> Shelves { get; } = [];

    /// <summary>The newest arrivals, across all series, for the row along the top.</summary>
    public ObservableCollection<ServerBookRow> Recent { get; } = [];

    public ObservableCollection<ServerLibraryInfo> Libraries { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisconnected))]
    public partial bool IsConnected { get; set; }

    public bool IsDisconnected => !IsConnected;

    [ObservableProperty]
    public partial bool HasRecent { get; set; }

    [ObservableProperty]
    public partial string Url { get; set; } = "";

    [ObservableProperty]
    public partial string Username { get; set; } = "";

    [ObservableProperty]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial string Token { get; set; } = "";

    /// <summary>Signing in with a pasted token instead of a password, for anyone who prefers it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesPassword))]
    public partial bool UsesToken { get; set; }

    public bool UsesPassword => !UsesToken;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string Status { get; set; } = "";

    public bool HasStatus => Status.Length > 0;

    [ObservableProperty]
    public partial double Progress { get; set; }

    /// <summary>True while a book is coming down — the one thing here worth interrupting.</summary>
    [ObservableProperty]
    public partial bool IsDownloading { get; set; }

    [ObservableProperty]
    public partial ServerLibraryInfo? SelectedLibrary { get; set; }

    public async Task LoadAsync()
    {
        Url = server.Url ?? "";
        IsConnected = await server.RestoreAsync();

        // Only the first time. Coming back from a download should not spend a minute re-reading a
        // shelf that has not changed.
        if (IsConnected && Shelves.Count == 0) await RefreshAsync();
    }

    [RelayCommand]
    private Task ConnectAsync() => GuardAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(Url)) throw new InvalidOperationException("Upiši adresu servera.");

        Status = "Povezujem se…";

        if (UsesToken)
        {
            if (string.IsNullOrWhiteSpace(Token))
                throw new InvalidOperationException("Zalijepi token sa stranice svog računa na serveru.");

            await server.ConnectWithTokenAsync(Url.Trim(), Token.Trim());
        }
        else
        {
            await server.SignInAsync(Url.Trim(), Username.Trim(), Password);
        }

        // Kept nowhere else, and cleared from the screen the moment it has been exchanged for a
        // token. Nothing in this app writes a password down.
        Password = "";
        Token = "";

        IsConnected = true;
        await RefreshAsync();
    });

    [RelayCommand]
    private Task CloseAsync() => Shell.Current.GoToAsync("..");

    [RelayCommand]
    private void Disconnect()
    {
        server.Disconnect();

        IsConnected = false;
        HasRecent = false;

        Shelves.Clear();
        Recent.Clear();
        Libraries.Clear();

        Status = "Odspojeno.";
    }

    [RelayCommand]
    private Task RefreshAsync() => GuardAsync(async () =>
    {
        Status = "Čitam biblioteke…";

        Libraries.Clear();
        foreach (var library in await server.GetLibrariesAsync()) Libraries.Add(library);

        AppLog.Info($"server: {Libraries.Count} book libraries at {server.Url}");

        if (Libraries.Count == 0)
        {
            Status = "Na serveru nema biblioteke s knjigama.";
            return;
        }

        // Assigning it is what triggers the load, so an already-chosen library needs the explicit
        // call — otherwise nothing changes and nothing happens.
        if (SelectedLibrary is null || !Libraries.Contains(SelectedLibrary)) SelectedLibrary = Libraries[0];
        else await LoadBooksAsync();
    });

    partial void OnSelectedLibraryChanged(ServerLibraryInfo? value)
    {
        if (value is not null) _ = LoadBooksAsync();
    }

    /// <summary>How many of the newest arrivals the top row shows.</summary>
    private const int RecentCount = 12;

    private Task LoadBooksAsync() => GuardAsync(async () =>
    {
        if (SelectedLibrary is not { } library) return;

        Shelves.Clear();
        Recent.Clear();
        HasRecent = false;

        Status = "Čitam knjige…";

        var found = new Progress<int>(count => Status = $"Čitam knjige… {count}");
        var books = await server.GetBooksAsync(library.Id, found);

        AppLog.Info($"server: {books.Count} books in '{library.Name}'");

        foreach (var book in Newest(books)) Recent.Add(Row(book));
        foreach (var shelf in Arrange(books)) Shelves.Add(shelf);

        HasRecent = Recent.Count > 0;

        Status = books.Count == 0
            ? $"Biblioteka „{library.Name}” je prazna."
            : $"{books.Count} knjiga u „{library.Name}”.";
    });

    private ServerBookRow Row(ServerBook book) => new(book, server.CoverUrl(book.Id));

    private static IEnumerable<ServerBook> Newest(IReadOnlyList<ServerBook> books) =>
        books.Where(b => b.AddedAt is not null)
            .OrderByDescending(b => b.AddedAt)
            .Take(RecentCount);

    /// <summary>
    /// Sorts the library onto shelves: each series in its own reading order, then everything else.
    ///
    /// Series first, because that is how anyone looks for the next one — and in sequence rather
    /// than alphabetically, since a series listed by title is a series you have to think about.
    /// Books in no series go last under one heading rather than each becoming a shelf of one.
    /// </summary>
    private IEnumerable<Shelf> Arrange(IReadOnlyList<ServerBook> books)
    {
        var series = books
            .Where(b => b.InSeries)
            .GroupBy(b => b.Series!)
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new Shelf(
                g.Key,
                [.. g.OrderBy(b => b.SequenceOrder).ThenBy(b => b.Title).Select(Row)]));

        var loose = books
            .Where(b => !b.InSeries)
            .OrderBy(b => b.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(Row)
            .ToList();

        foreach (var shelf in series) yield return shelf;

        if (loose.Count > 0) yield return new Shelf("Bez serije", loose);
    }

    /// <summary>The download in flight, so it can be stopped.</summary>
    private CancellationTokenSource? _downloading;

    [RelayCommand]
    private void CancelDownload()
    {
        if (_downloading is null) return;

        _downloading.Cancel();
        Status = "Prekidam preuzimanje…";
    }

    [RelayCommand]
    private Task ImportAsync(ServerBookRow? row) => GuardAsync(async () =>
    {
        if (row is null) return;

        if (!row.CanImport)
        {
            Status = row.Note;
            return;
        }

        // One at a time, and said rather than silently ignored. Two downloads over a phone's
        // connection finish later than two in a row, and only one progress bar can be believed.
        if (IsDownloading)
        {
            Status = "Već preuzimam jednu knjigu — pričekaj ili je prekini.";
            return;
        }

        // Asked before it starts. A tap that begins a three-hundred-megabyte download with no
        // warning is a tap nobody meant to make.
        var confirmed = await Shell.Current.DisplayAlertAsync(
            row.Title,
            row.Note.Length > 0 ? $"Preuzeti sa servera? ({row.Note})" : "Preuzeti sa servera?",
            "Preuzmi",
            "Odustani");

        if (!confirmed) return;

        using var downloading = new CancellationTokenSource();

        _downloading = downloading;
        IsDownloading = true;

        try
        {
            var progress = new Progress<ImportProgress>(p =>
            {
                Status = p.Message;
                Progress = p.Fraction;
            });

            await server.ImportAsync(row.Book, progress, downloading.Token);

            Status = $"„{row.Title}” je u biblioteci.";
        }
        catch (OperationCanceledException)
        {
            // The half-finished file is already gone: whatever was written is deleted by the code
            // that was writing it, whichever way the download ended.
            Status = $"Preuzimanje knjige „{row.Title}” je prekinuto.";
        }
        finally
        {
            _downloading = null;
            IsDownloading = false;
            Progress = 0;
        }
    });

    /// <summary>
    /// How deep the guarded calls are nested.
    ///
    /// Counted rather than flagged, because these call each other: connecting reads the libraries,
    /// which loads the books. A plain "already busy, do nothing" turned that into a screen that
    /// connected, said it was reading the libraries, and then stopped — the inner call saw the flag
    /// its own caller had set and returned without doing anything at all.
    ///
    /// Nothing is lost by allowing it: a second tap on the same command is already refused by the
    /// command itself, which is where that belongs.
    /// </summary>
    private int _depth;

    /// <summary>
    /// Runs a command, turning a failure into a line on screen rather than a crash — and naming the
    /// one failure the user can do something about.
    /// </summary>
    private async Task GuardAsync(Func<Task> action)
    {
        _depth++;
        IsBusy = true;

        try
        {
            await action();
        }
        catch (ServerException ex)
        {
            AppLog.Error("server", ex);

            Status = ex.Message;
            if (ex.NeedsSignIn) IsConnected = false;
        }
        catch (Exception ex)
        {
            AppLog.Error("server", ex);
            Status = ex.Message;
        }
        finally
        {
            if (--_depth == 0)
            {
                IsBusy = false;
                Progress = 0;
            }
        }
    }
}

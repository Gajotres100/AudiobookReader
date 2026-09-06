using System.Collections.ObjectModel;
using AudioBookReader.App.Services;
using AudioBookReader.Core.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioBookReader.App.ViewModels;

/// <summary>One book on the server, as the list shows it.</summary>
public class ServerBookRow(ServerBook book)
{
    public ServerBook Book { get; } = book;

    public string Title { get; } = book.Title;

    public string Author { get; } = book.Author ?? "";

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

public partial class ServerViewModel(ServerConnection server) : ObservableObject
{
    public ObservableCollection<ServerBookRow> Books { get; } = [];

    public ObservableCollection<ServerLibraryInfo> Libraries { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisconnected))]
    public partial bool IsConnected { get; set; }

    public bool IsDisconnected => !IsConnected;

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

    [ObservableProperty]
    public partial ServerLibraryInfo? SelectedLibrary { get; set; }

    public async Task LoadAsync()
    {
        Url = server.Url ?? "";
        IsConnected = await server.RestoreAsync();

        if (IsConnected) await RefreshAsync();
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
        Books.Clear();
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

    private Task LoadBooksAsync() => GuardAsync(async () =>
    {
        if (SelectedLibrary is not { } library) return;

        Books.Clear();
        Status = "Čitam knjige…";

        var found = new Progress<int>(count => Status = $"Čitam knjige… {count}");
        var books = await server.GetBooksAsync(library.Id, found);

        foreach (var book in books) Books.Add(new ServerBookRow(book));

        AppLog.Info($"server: {Books.Count} books in '{library.Name}'");

        Status = Books.Count == 0
            ? $"Biblioteka „{library.Name}” je prazna."
            : $"{Books.Count} knjiga u „{library.Name}”.";
    });

    [RelayCommand]
    private Task ImportAsync(ServerBookRow? row) => GuardAsync(async () =>
    {
        if (row is null) return;

        if (!row.CanImport)
        {
            Status = row.Note;
            return;
        }

        var progress = new Progress<ImportProgress>(p =>
        {
            Status = p.Message;
            Progress = p.Fraction;
        });

        await server.ImportAsync(row.Book, progress);

        Progress = 0;
        Status = $"„{row.Title}” je u biblioteci.";
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

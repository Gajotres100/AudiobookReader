using AudioBookReader.App.Resources.Strings;
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
        ? string.Format(Strings.Server_SplitFiles, book.AudioFileCount)
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

    public string Summary { get; } = books.Count == 1
        ? Strings.Server_OneBook
        : string.Format(Strings.Server_BookCount, books.Count);
}

/// <summary>One configured server, as the row at the top of the server screen shows it.</summary>
public record ServerChip(string Id, string Name, bool IsActive);

public partial class ServerViewModel(ServerConnections servers) : ObservableObject
{
    /// <summary>
    /// The server on screen. There is always one: with none configured, a new one is started, and
    /// its sign-in form is what the screen shows.
    /// </summary>
    private ServerConnection server => servers.Active ?? servers.Add()!;

    /// <summary>The servers to switch between, up to five.</summary>
    public ObservableCollection<ServerChip> Servers { get; } = [];

    /// <summary>Shown once there is anything to switch between or add to.</summary>
    [ObservableProperty]
    public partial bool ShowsServers { get; set; }

    [ObservableProperty]
    public partial bool CanAddServer { get; set; }

    /// <summary>What to call a server being added, so five of them can be told apart. Optional.</summary>
    [ObservableProperty]
    public partial string ServerName { get; set; } = "";

    /// <summary>
    /// The shelves, in the order they are read: the newest arrivals, then each series, then
    /// everything belonging to no series.
    ///
    /// One kind of thing rather than a special row plus a grouped list. Every shelf is a title and
    /// a row of covers, so there is one template, one scrolling behaviour, and nothing that behaves
    /// differently because of where it happens to sit.
    /// </summary>
    public ObservableCollection<Shelf> Shelves { get; } = [];

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

    /// <summary>
    /// Whether to keep the sign-in itself, not only the tokens.
    ///
    /// On by default, because the alternative is worse than it sounds: the tokens last thirty
    /// unused days, and a book app can easily go a month unopened — after which everything has to
    /// be typed again for no reason the user can see.
    /// </summary>
    [ObservableProperty]
    public partial bool RemembersSignIn { get; set; } = true;

    /// <summary>Signing in with a pasted token instead of a password, for anyone who prefers it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesPassword))]
    public partial bool UsesToken { get; set; }

    public bool UsesPassword => !UsesToken;

    /// <summary>
    /// Skip certificate validation for this server — for a self-signed one with no other way in.
    ///
    /// Saved as it is toggled, but only read by the connection itself once, when that singleton is
    /// first built — so a change here takes hold from the app's next launch, not mid-session. Worth
    /// saying plainly in the warning next to it rather than leaving someone to discover it by
    /// pressing Connect and finding nothing changed.
    /// </summary>
    [ObservableProperty]
    public partial bool TrustAnyCertificate { get; set; }

    partial void OnTrustAnyCertificateChanged(bool value) => server.Account.TrustAnyCertificate = value;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string Status { get; set; } = "";

    public bool HasStatus => Status.Length > 0;

    /// <summary>
    /// Signing in, specifically — shown beside the Connect button rather than on the shared status
    /// line at the foot of the page, which is a single truncated line far from where the user is
    /// looking and was read as the app doing nothing.
    /// </summary>
    [ObservableProperty]
    public partial bool IsConnecting { get; set; }

    /// <summary>Why the last sign-in failed, shown in full under the button until the next attempt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConnectError))]
    public partial string ConnectError { get; set; } = "";

    public bool HasConnectError => ConnectError.Length > 0;

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial ServerLibraryInfo? SelectedLibrary { get; set; }

    public async Task LoadAsync()
    {
        ShowServers();

        Url = server.Url ?? "";
        ServerName = server.Account.Name ?? "";
        // Reflects what was chosen last time, and starts on for a server never connected to.
        RemembersSignIn = await server.RemembersSignInAsync() || server.Url is null;
        TrustAnyCertificate = server.Account.TrustAnyCertificate;

        IsConnected = await server.RestoreAsync();

        // Only the first time. Coming back from a download should not spend a minute re-reading a
        // shelf that has not changed.
        if (IsConnected && Shelves.Count == 0) await RefreshAsync();
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        ConnectError = "";
        IsConnecting = true;

        try
        {
            await GuardAsync(SignInAsync, onError: message => ConnectError = message);
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private async Task SignInAsync()
    {
        if (string.IsNullOrWhiteSpace(Url)) throw new InvalidOperationException(Strings.Server_EnterAddress);

        Status = Strings.Server_Connecting;

        if (UsesToken)
        {
            if (string.IsNullOrWhiteSpace(Token))
                throw new InvalidOperationException(Strings.Server_PasteToken);

            await server.ConnectWithTokenAsync(Url.Trim(), Token.Trim());
        }
        else
        {
            await server.SignInAsync(Url.Trim(), Username.Trim(), Password, RemembersSignIn);
        }

        server.Account.Name = ServerName;

        // Kept nowhere else, and cleared from the screen the moment it has been exchanged for a
        // token. Nothing in this app writes a password down.
        Password = "";
        Token = "";

        IsConnected = true;
        ShowServers();

        await RefreshAsync();
    }

    /// <summary>Rebuilds the row of servers from what is configured now.</summary>
    private void ShowServers()
    {
        var active = server.Id;

        Servers.Clear();
        foreach (var each in servers.All)
            Servers.Add(new ServerChip(
                each.Id,
                each.Account.IsConfigured ? each.Name : Strings.Server_NewServer,
                each.Id == active));

        CanAddServer = servers.CanAdd;
        ShowsServers = servers.IsConfigured;
    }

    /// <summary>Shows another server's shelves, or its sign-in if it has not been signed in to yet.</summary>
    [RelayCommand]
    private async Task SwitchServerAsync(ServerChip? chip)
    {
        if (chip is null || chip.Id == server.Id) return;

        servers.Activate(chip.Id);
        Forget();

        await LoadAsync();
    }

    /// <summary>Starts adding another server: an empty sign-in form, kept only once it connects.</summary>
    [RelayCommand]
    private async Task AddServerAsync()
    {
        if (servers.Add() is null) return;

        Forget();
        Url = Username = Password = Token = ServerName = "";

        await LoadAsync();
    }

    /// <summary>Clears what the screen shows of the server it is leaving.</summary>
    private void Forget()
    {
        IsConnected = false;
        ConnectError = "";
        Status = "";

        Shelves.Clear();
        Libraries.Clear();
        SelectedLibrary = null;
    }

    [RelayCommand]
    private Task CloseAsync() => Shell.Current.GoToAsync("..");

    /// <summary>
    /// Forgets the server on screen, and moves to another if there is one. Books that came from it
    /// stay in the library.
    /// </summary>
    [RelayCommand]
    private async Task DisconnectAsync()
    {
        servers.Remove(server.Id);
        Forget();

        await LoadAsync();

        Status = Strings.Server_Disconnected;
    }

    [RelayCommand]
    private Task RefreshAsync() => GuardAsync(async () =>
    {
        Status = Strings.Server_ReadingLibraries;

        Libraries.Clear();
        foreach (var library in await server.GetLibrariesAsync()) Libraries.Add(library);

        AppLog.Info($"server: {Libraries.Count} book libraries at {server.Url}");

        if (Libraries.Count == 0)
        {
            Status = Strings.Server_NoBookLibraries;
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
        Status = Strings.Server_ReadingBooks;

        var found = new Progress<int>(count => Status = string.Format(Strings.Server_ReadingBooksCount, count));
        var books = await server.GetBooksAsync(library.Id, found);

        AppLog.Info($"server: {books.Count} books in '{library.Name}'");

        foreach (var shelf in Arrange(books)) Shelves.Add(shelf);

        Status = books.Count == 0
            ? string.Format(Strings.Server_LibraryEmpty, library.Name)
            : string.Format(Strings.Server_BooksInLibrary, books.Count, library.Name);
    });

    private ServerBookRow Row(ServerBook book) => new(book, server.CoverUrl(book.Id));

    /// <summary>
    /// Sorts the library onto shelves: the newest first, then each series in its own reading order,
    /// then everything else.
    ///
    /// Series in sequence rather than alphabetically, since a series listed by title is a series
    /// you have to think about. Books in no series go last under one heading rather than each
    /// becoming a shelf of one.
    /// </summary>
    private IEnumerable<Shelf> Arrange(IReadOnlyList<ServerBook> books)
    {
        var newest = books
            .Where(b => b.AddedAt is not null)
            .OrderByDescending(b => b.AddedAt)
            .Take(RecentCount)
            .Select(Row)
            .ToList();

        if (newest.Count > 0) yield return new Shelf(Strings.Server_RecentlyAdded, newest);

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

        if (loose.Count > 0) yield return new Shelf(Strings.Server_NoSeries, loose);
    }

    /// <summary>
    /// Opens the book rather than fetching it.
    ///
    /// A tap used to begin a three-hundred-megabyte download on the spot. What is about to come
    /// over a phone's connection deserves a page first — and the reason a book cannot come at all
    /// belongs there too, rather than in a button that refuses.
    /// </summary>
    [RelayCommand]
    private static Task ImportAsync(ServerBookRow? row) =>
        row is null
            ? Task.CompletedTask
            : Shell.Current.GoToAsync($"serverbook?id={Uri.EscapeDataString(row.Book.Id)}");

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
    private async Task GuardAsync(Func<Task> action, Action<string>? onError = null)
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
            onError?.Invoke(ex.Message);
            if (ex.NeedsSignIn) IsConnected = false;
        }
        catch (Exception ex)
        {
            AppLog.Error("server", ex);
            Status = ex.Message;
            onError?.Invoke(ex.Message);
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

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using AudioBookReader.App.Resources.Strings;
using AudioBookReader.Core.Books;
using AudioBookReader.Core.Data;

namespace AudioBookReader.App.Services;

/// <summary>Another device on this network running the app, found by asking around.</summary>
public record NearbyDevice(string Name, IPAddress Address);

/// <summary>
/// Sends a book's alignment to another device on the same network, and receives one.
///
/// Made for the television: it cannot align at all (no 32-bit speech recognition), but it can
/// follow along perfectly well with an alignment a phone made. The phone asks the network who is
/// there, sends the alignment to the one chosen, and the television asks whoever is in front of it
/// before taking it — and takes it only for a book made of exactly the same files.
///
/// Discovery is one broadcast and whoever answers. iPhones and iPads may not broadcast without an
/// entitlement Apple hands out on request, so an address can always be typed instead; every device
/// shows its own in Settings.
/// </summary>
public sealed class AlignmentShare(
    LibraryDatabase database,
    SyncMapStore syncMaps,
    LibraryService library)
{
    private const int DiscoveryPort = 47350;
    private const int TransferPort = 47351;
    private const string Question = "SYNCBOOK?";
    private const string AnswerPrefix = "SYNCBOOK!";

    private CancellationTokenSource? _listening;
    private Task? _accepting;

    /// <summary>
    /// One package at a time. Two devices sending at once — or one sending twice — would otherwise
    /// both write the same book's map and chapters, and whichever finished second would win half of
    /// each.
    /// </summary>
    private readonly SemaphoreSlim _considering = new(1, 1);

    /// <summary>The book a QR code was shown for, so what arrives with its key is taken for that book only.</summary>
    private int? _pairedBook;

    // ---- Pairing by QR code ----

    private string? _key;
    private DateTime _keyExpires;

    /// <summary>Raised when an alignment has been taken, with the book's title, for a screen waiting on one.</summary>
    public event EventHandler<string>? Received;

    /// <summary>
    /// Starts waiting for an alignment sent by QR code: a fresh one-time code, and where this
    /// device can be reached. Null when it is on no local network at all.
    ///
    /// The code lasts while the QR code is on screen, and a quarter of an hour at most. A package
    /// carrying it is taken without asking — whoever sent it read it off this screen.
    /// </summary>
    public PairingCode? BeginPairing(int? bookId = null)
    {
        var addresses = LocalAddresses();
        if (addresses.Count == 0) return null;

        _key = PairingCode.NewKey();
        _keyExpires = DateTime.UtcNow.AddMinutes(15);
        _pairedBook = bookId;

        StartListening();
        return new PairingCode(addresses, TransferPort, _key, DeviceName);
    }

    public void EndPairing()
    {
        _key = null;
        _pairedBook = null;
    }

    private bool CarriesTheKey(AlignmentPackage package) =>
        _key is { } key && package.Key == key && DateTime.UtcNow < _keyExpires;

    /// <summary>This device as others see it.</summary>
    public static string DeviceName =>
        string.IsNullOrWhiteSpace(DeviceInfo.Current.Name) ? DeviceInfo.Current.Model : DeviceInfo.Current.Name;

    /// <summary>This device's addresses on the local network, for typing in on another device.</summary>
    public static IReadOnlyList<IPAddress> LocalAddresses()
    {
        try
        {
            return
            [
                .. NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up
                                && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Select(a => a.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a)),
            ];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Home and office networks, not a VPN's or a mobile carrier's.</summary>
    private static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 192 && b[1] == 168 || b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31;
    }

    // ---- Receiving ----

    /// <summary>Starts answering discovery and accepting alignments, for as long as the app runs.</summary>
    /// <summary>
    /// Listens for alignments — and listens again if listening has stopped.
    ///
    /// Asked every time a QR code is shown, not only at start: iOS closes an app's sockets while it
    /// is in the background, and a listener that died then stayed dead, so a device that had once
    /// been put away quietly stopped receiving anything at all.
    /// </summary>
    public void StartListening()
    {
        if (_listening is not null && _accepting is { IsCompleted: false }) return;

        _listening?.Cancel();
        _listening = new CancellationTokenSource();
        var ct = _listening.Token;

        _ = Task.Run(() => AnswerDiscoveryAsync(ct), ct);
        _accepting = Task.Run(() => AcceptAsync(ct), ct);
    }

    private static async Task AnswerDiscoveryAsync(CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, DiscoveryPort)) { EnableBroadcast = true };

            while (!ct.IsCancellationRequested)
            {
                var received = await udp.ReceiveAsync(ct);
                if (Encoding.UTF8.GetString(received.Buffer) != Question) continue;

                var answer = Encoding.UTF8.GetBytes(AnswerPrefix + DeviceName);
                await udp.SendAsync(answer, received.RemoteEndPoint, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Not being findable is not fatal: an address typed by hand still reaches the transfer
            // port below.
            AppLog.Info($"alignment share: not answering discovery ({ex.Message})");
        }
    }

    private async Task AcceptAsync(CancellationToken ct)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, TransferPort);

            // So a listener started again right after one died can have the port straight back.
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Start();

            using var registration = ct.Register(listener.Stop);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(ct);
                    _ = Task.Run(() => ReceiveAsync(client, ct), ct);
                }
            }
            finally
            {
                listener.Stop();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Info($"alignment share: not receiving ({ex.Message})");
        }
    }

    private async Task ReceiveAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();

                // Long enough for someone across the room to find the remote and answer.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));

                if (await AlignmentTransfer.ReadPackageAsync(stream, timeout.Token) is not { } package) return;

                var reply = await ConsiderAsync(package);
                await AlignmentTransfer.WriteReplyAsync(stream, reply, timeout.Token);
            }
            catch (Exception ex)
            {
                AppLog.Info($"alignment share: receiving failed ({ex.Message})");
            }
        }
    }

    private async Task<AlignmentReply> ConsiderAsync(AlignmentPackage package)
    {
        await _considering.WaitAsync();

        try
        {
            return await ConsiderOneAsync(package);
        }
        finally
        {
            _considering.Release();
        }
    }

    private async Task<AlignmentReply> ConsiderOneAsync(AlignmentPackage package)
    {
        if (await library.FindByPairAsync(package.AudioHash, package.EbookHash) is not { } book)
        {
            AppLog.Info($"alignment share: '{package.Title}' from {package.From} — no book made of the same files here");
            return new AlignmentReply(false, AlignmentTransfer.NoSuchBook);
        }

        // Shown for one book, taken for that book: an alignment for another sent with this code is
        // a mistake on the sending side, not something to take quietly for a book nobody opened.
        if (CarriesTheKey(package) && _pairedBook is { } only && book.Id != only)
        {
            AppLog.Info($"alignment share: '{package.Title}' from {package.From} is not the book whose code was shown");
            return new AlignmentReply(false, AlignmentTransfer.NoSuchBook);
        }

        // Sent with the code this device is showing: the person asked when they scanned it.
        var accept = CarriesTheKey(package) || await MainThread.InvokeOnMainThreadAsync(() => Dialogs.AskAsync(
            Strings.Share_ReceiveTitle,
            string.Format(Strings.Share_ReceiveBody, package.From, book.Title),
            Strings.Share_Accept,
            Strings.Share_Decline));

        if (!accept) return new AlignmentReply(false, AlignmentTransfer.Declined);

        var adopted = await library.AdoptAlignmentAsync(book.Id, package.Map, package.Chapters);
        AppLog.Info($"alignment share: '{book.Title}' from {package.From} {(adopted ? "taken" : "refused")}");

        if (adopted) Received?.Invoke(this, book.Title);

        return adopted ? new AlignmentReply(true) : new AlignmentReply(false, AlignmentTransfer.NoSuchBook);
    }

    // ---- Sending ----

    /// <summary>Asks the network who is there. Takes about as long as <paramref name="wait"/>.</summary>
    public static async Task<IReadOnlyList<NearbyDevice>> FindAsync(TimeSpan wait)
    {
        var found = new Dictionary<IPAddress, NearbyDevice>();
        var own = LocalAddresses().ToHashSet();

        try
        {
            using var udp = new UdpClient(0) { EnableBroadcast = true };
            var question = Encoding.UTF8.GetBytes(Question);

            // Both the general broadcast and each network's own: some routers pass only one.
            var targets = new List<IPEndPoint> { new(IPAddress.Broadcast, DiscoveryPort) };
            targets.AddRange(own.Select(a =>
            {
                var b = a.GetAddressBytes();
                return new IPEndPoint(new IPAddress([b[0], b[1], b[2], 255]), DiscoveryPort);
            }));

            foreach (var target in targets)
            {
                try { await udp.SendAsync(question, target); }
                catch (Exception ex) { AppLog.Info($"alignment share: could not ask {target} ({ex.Message})"); }
            }

            using var until = new CancellationTokenSource(wait);

            while (!until.IsCancellationRequested)
            {
                UdpReceiveResult answer;
                try { answer = await udp.ReceiveAsync(until.Token); }
                catch (OperationCanceledException) { break; }

                var text = Encoding.UTF8.GetString(answer.Buffer);
                if (!text.StartsWith(AnswerPrefix, StringComparison.Ordinal)) continue;

                var address = answer.RemoteEndPoint.Address;
                if (own.Contains(address)) continue;

                found[address] = new NearbyDevice(text[AnswerPrefix.Length..], address);
            }
        }
        catch (Exception ex)
        {
            AppLog.Info($"alignment share: looking for devices failed ({ex.Message})");
        }

        return [.. found.Values];
    }

    /// <summary>
    /// Sends a book's alignment and waits for the other device to answer.
    /// </summary>
    /// <returns>The other side's answer; not accepted with a null reason when it could not be reached.</returns>
    /// <summary>A book's alignment, packed to leave this device.</summary>
    private async Task<AlignmentPackage> PackAsync(int bookId, string? key = null)
    {
        var book = await database.GetBookAsync(bookId) ?? throw new InvalidOperationException("No such book.");
        var map = await syncMaps.LoadAsync(bookId) ?? throw new InvalidOperationException(Strings.Share_NothingToSend);

        var chapters = await database.GetChaptersAsync(bookId);
        return new AlignmentPackage(
            DeviceName,
            book.Title,
            book.AudioHash,
            book.EbookHash,
            [.. chapters.Select(c => new ChapterRange(c.Index, c.TextStart, c.TextEnd))],
            map,
            key);
    }

    /// <summary>
    /// Sends to the device whose QR code was scanned, trying each address it listed until one
    /// answers — it may be on more than one network, and only one of them is shared with this one.
    /// </summary>
    public async Task<AlignmentReply> SendAsync(int bookId, PairingCode code, CancellationToken ct = default)
    {
        var reply = new AlignmentReply(false);

        foreach (var address in code.Addresses)
        {
            reply = await SendAsync(bookId, address, ct, code.Key, code.Port);

            // An answer of any kind means this was the right address; only silence means try the next.
            if (reply.Accepted || reply.Reason is not null) break;
        }

        return reply;
    }

    public async Task<AlignmentReply> SendAsync(
        int bookId, IPAddress to, CancellationToken ct = default, string? key = null, int port = TransferPort)
    {
        var package = await PackAsync(bookId, key);

        try
        {
            using var client = new TcpClient();

            using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connecting.CancelAfter(TimeSpan.FromSeconds(5));
                await client.ConnectAsync(to, port, connecting.Token);
            }

            var stream = client.GetStream();
            await AlignmentTransfer.WritePackageAsync(stream, package, ct);

            // The other side asks a person, so this waits as long as they might take.
            using var answering = CancellationTokenSource.CreateLinkedTokenSource(ct);
            answering.CancelAfter(TimeSpan.FromMinutes(2));

            return await AlignmentTransfer.ReadReplyAsync(stream, answering.Token) ?? new AlignmentReply(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            AppLog.Info($"alignment share: sending to {to} failed ({ex.Message})");
            return new AlignmentReply(false);
        }
    }

    // ---- As a file ----

    /// <summary>
    /// Writes a book's alignment to a file for sending any way at all, and returns where it is. In
    /// the cache, since the share sheet copies it wherever it goes.
    /// </summary>
    public async Task<string> ExportAsync(int bookId, CancellationToken ct = default)
    {
        var package = await PackAsync(bookId);
        var path = Path.Combine(FileSystem.CacheDirectory, AlignmentFile.FileNameFor(package.Title));

        await using (var file = File.Create(path))
            await AlignmentFile.WriteAsync(file, package, ct);

        return path;
    }

    /// <summary>
    /// Takes an alignment from a file someone sent, for whichever book here is made of the files it
    /// names. Returns what to tell the person: taken, or why not.
    /// </summary>
    public async Task<string> ImportAsync(Stream file, CancellationToken ct = default)
    {
        if (await AlignmentFile.ReadAsync(file, ct) is not { } package) return Strings.Share_NotAnAlignment;

        if (await library.FindByPairAsync(package.AudioHash, package.EbookHash) is not { } book)
            return string.Format(Strings.Share_FileNoSuchBook, package.Title);

        if (!await library.AdoptAlignmentAsync(book.Id, package.Map, package.Chapters))
            return string.Format(Strings.Share_FileNoSuchBook, package.Title);

        AppLog.Info($"alignment share: '{book.Title}' taken from a file made on {package.From}");
        return string.Format(Strings.Share_FileTaken, book.Title, package.From);
    }
}

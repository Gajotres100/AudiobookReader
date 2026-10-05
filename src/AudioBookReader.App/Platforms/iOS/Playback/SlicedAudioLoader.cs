using AVFoundation;
using AudioBookReader.App.Services;
using CoreFoundation;
using Foundation;

namespace AudioBookReader.App.Platforms.iOS.Playback;

/// <summary>
/// Plays the narration of an EPUB 3 straight out of the package on the server.
///
/// The recording is a stretch of the package file, and AVFoundation can only be pointed at whole
/// files. So the item is given an address of a made-up scheme, which AVFoundation cannot fetch and
/// hands to this instead: every range it asks for is read from that stretch of the package — through
/// the same seekable stream that hashing and copying use, which signs each request and renews its
/// token as it goes — and handed back. To the player it is a file of the recording's own length.
///
/// Android does the same by moving the player's own requests (see its PlaybackService).
/// </summary>
internal sealed class SlicedAudioLoader : AVAssetResourceLoaderDelegate
{
    private const string Scheme = "abrslice";

    /// <summary>A piece handed to the player at a time: enough to keep it fed, little enough to stop on a seek.</summary>
    private const int Piece = 256 * 1024;

    private static readonly DispatchQueue Queue = new("abr.sliced-audio");

    private readonly string _location;
    private readonly long _length;

    /// <summary>One read at a time through the one stream: a seek cancels the read it replaces.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly Dictionary<IntPtr, CancellationTokenSource> _running = [];
    private Stream? _stream;
    private string? _contentType;

    private SlicedAudioLoader(string location, long length)
    {
        _location = location;
        _length = length;
    }

    /// <summary>
    /// An item that plays the stretch a streamed location names. The loader is returned too: the
    /// asset holds its delegate weakly, and a collected loader would leave the player waiting.
    /// </summary>
    public static AVPlayerItem CreateItem(string location, out SlicedAudioLoader loader)
    {
        if (!StreamedAudio.TrySlice(location, out _, out var length))
            throw new ArgumentException($"Not a stretch of a file: {location}", nameof(location));

        loader = new SlicedAudioLoader(location, length);

        var asset = new AVUrlAsset(NSUrl.FromString($"{Scheme}://narration/{Guid.NewGuid():N}")!);
        asset.ResourceLoader.SetDelegate(loader, Queue);

        return new AVPlayerItem(asset);
    }

    public override bool ShouldWaitForLoadingOfRequestedResource(
        AVAssetResourceLoader resourceLoader, AVAssetResourceLoadingRequest loadingRequest)
    {
        var cancellation = new CancellationTokenSource();
        lock (_running) _running[loadingRequest.Handle] = cancellation;

        _ = ServeAsync(loadingRequest, cancellation.Token);
        return true;
    }

    public override void DidCancelLoadingRequest(AVAssetResourceLoader resourceLoader, AVAssetResourceLoadingRequest loadingRequest)
    {
        lock (_running)
        {
            if (_running.Remove(loadingRequest.Handle, out var cancellation)) cancellation.Cancel();
        }
    }

    private async Task ServeAsync(AVAssetResourceLoadingRequest request, CancellationToken ct)
    {
        try
        {
            await _gate.WaitAsync(ct);

            try
            {
                _stream ??= await (StreamedAudio.Source ?? throw new InvalidOperationException("Not connected to the server."))
                    .OpenAsync(_location, ct);

                if (request.ContentInformationRequest is { } info)
                {
                    info.ContentType = _contentType ??= await SniffAsync(_stream, ct);
                    info.ContentLength = _length;
                    info.ByteRangeAccessSupported = true;
                }

                if (request.DataRequest is { } data)
                {
                    var offset = data.RequestedOffset;
                    var left = data.RequestsAllDataToEndOfResource
                        ? _length - offset
                        : Math.Min((long)data.RequestedLength, _length - offset);

                    _stream.Seek(offset, SeekOrigin.Begin);

                    var buffer = new byte[Piece];

                    while (left > 0)
                    {
                        ct.ThrowIfCancellationRequested();

                        var read = await _stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct);
                        if (read == 0) break;

                        data.Respond(NSData.FromArray(read == buffer.Length ? buffer : buffer[..read]));
                        left -= read;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The next request starts afresh, in case the connection was what failed. Inside the
                // gate, so no other request is reading from it while it goes.
                _stream?.Dispose();
                _stream = null;
                throw;
            }
            finally
            {
                _gate.Release();
            }

            request.FinishLoading();
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the player, which has moved on; it is not waiting for an answer.
        }
        catch (Exception ex)
        {
            AppLog.Error("streaming narration from the server", ex);
            request.FinishLoadingWithError(new NSError(new NSString("abr.sliced-audio"), 1));
        }
        finally
        {
            lock (_running) _running.Remove(request.Handle);
        }
    }

    /// <summary>
    /// What the recording is, from its first bytes: AVFoundation wants a type before it will read a
    /// file it cannot see the name of. MP4 audio is what a read-along package almost always holds.
    /// </summary>
    private async Task<string> SniffAsync(Stream stream, CancellationToken ct)
    {
        var head = new byte[12];
        stream.Seek(0, SeekOrigin.Begin);
        var read = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);

        if (read >= 8 && head[4] == 'f' && head[5] == 't' && head[6] == 'y' && head[7] == 'p') return "com.apple.m4a-audio";
        if (read >= 3 && head[0] == 'I' && head[1] == 'D' && head[2] == '3') return "public.mp3";
        if (read >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0) return "public.mp3";

        return "com.apple.m4a-audio";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_running)
            {
                foreach (var cancellation in _running.Values) cancellation.Cancel();
                _running.Clear();
            }

            _stream?.Dispose();
        }

        base.Dispose(disposing);
    }
}

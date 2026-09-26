using Android.Content;
using AudioBookReader.App.Platforms.Android.Services;

namespace AudioBookReader.App.Services;

public partial class DownloadQueue
{
    private partial void StartCore(
        string serverId, string itemId, string title, bool toAppStorage, bool wantAudio, bool wantEbook, int? attachTo)
    {
        var context = global::Android.App.Application.Context;

        var intent = new Intent(context, typeof(DownloadService))
            .SetAction(DownloadService.ActionStart)
            .PutExtra(DownloadService.ExtraServerId, serverId)
            .PutExtra(DownloadService.ExtraItemId, itemId)
            .PutExtra(DownloadService.ExtraTitle, title)
            .PutExtra(DownloadService.ExtraAppStorage, toAppStorage)
            .PutExtra(DownloadService.ExtraWantAudio, wantAudio)
            .PutExtra(DownloadService.ExtraWantEbook, wantEbook)
            .PutExtra(DownloadService.ExtraAttachTo, attachTo ?? 0);

        // A foreground service, so the transfer survives the screen going off. This is the whole
        // point of the class: as a plain task it was suspended along with the app.
        context.StartForegroundService(intent);
    }

    public partial void Stop()
    {
        var context = global::Android.App.Application.Context;
        var intent = new Intent(context, typeof(DownloadService)).SetAction(DownloadService.ActionStop);

        context.StartService(intent);
    }
}

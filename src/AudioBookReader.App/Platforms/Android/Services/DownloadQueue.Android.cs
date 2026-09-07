using Android.Content;
using AudioBookReader.App.Platforms.Android.Services;

namespace AudioBookReader.App.Services;

public partial class DownloadQueue
{
    private partial void StartCore(string itemId, string title, bool toAppStorage)
    {
        var context = global::Android.App.Application.Context;

        var intent = new Intent(context, typeof(DownloadService))
            .SetAction(DownloadService.ActionStart)
            .PutExtra(DownloadService.ExtraItemId, itemId)
            .PutExtra(DownloadService.ExtraTitle, title)
            .PutExtra(DownloadService.ExtraAppStorage, toAppStorage);

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

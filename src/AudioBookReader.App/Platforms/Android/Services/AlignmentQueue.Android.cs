using Android.Content;
using AudioBookReader.App.Platforms.Android.Alignment;

namespace AudioBookReader.App.Services;

public partial class AlignmentQueue
{
    private partial void StartCore(int bookId)
    {
        var context = global::Android.App.Application.Context;
        var intent = new Intent(context, typeof(AlignmentService));
        intent.SetAction(AlignmentService.ActionStart);
        intent.PutExtra(AlignmentService.ExtraBookId, bookId);

        // Started as a foreground service so the work survives the app going to the background,
        // which is exactly where a user leaves it while listening to the first chapter.
        context.StartForegroundService(intent);
    }


    public partial void Stop()
    {
        var context = global::Android.App.Application.Context;
        var intent = new Intent(context, typeof(AlignmentService));
        intent.SetAction(AlignmentService.ActionStop);

        context.StartService(intent);
    }
}

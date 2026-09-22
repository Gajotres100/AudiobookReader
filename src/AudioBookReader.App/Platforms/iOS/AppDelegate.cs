using AudioBookReader.App.Platforms.iOS.Alignment;
using Foundation;
using UIKit;

namespace AudioBookReader.App;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        // Must happen before this method returns — Apple refuses a BGTaskScheduler registration
        // made any later than app launch.
        BackgroundAlignmentScheduler.Register();

        return base.FinishedLaunching(application, launchOptions);
    }

    public override void DidEnterBackground(UIApplication application)
    {
        // Asks for a future opportunity to continue alignment unconditionally — harmless when
        // nothing was running (BackgroundAlignmentScheduler.Handle completes at once when there is
        // no book to resume), and the one place guaranteed to fire whenever the app backgrounds,
        // regardless of when or how alignment was started.
        BackgroundAlignmentScheduler.ScheduleNext();

        base.DidEnterBackground(application);
    }
}

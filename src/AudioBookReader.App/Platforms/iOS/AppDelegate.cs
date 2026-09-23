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

    public override void OnActivated(UIApplication application)
    {
        AppForeground.IsActive = true;
        base.OnActivated(application);
    }

    public override void OnResignActivation(UIApplication application)
    {
        AppForeground.IsActive = false;
        base.OnResignActivation(application);
    }

    public override void DidEnterBackground(UIApplication application)
    {
        // Asks for a future opportunity to continue alignment unconditionally — harmless when
        // nothing was running (the handler completes at once when there is no book to resume), and
        // the one place guaranteed to fire whenever the app backgrounds.
        BackgroundAlignmentScheduler.ScheduleNext();

        base.DidEnterBackground(application);
    }
}

using UIKit;

namespace AudioBookReader.App.Services;

/// <summary>
/// Where every picker in this folder finds something to present itself on. Plain UIKit rather than
/// a MAUI helper, since none of the MAUI assemblies in this SDK version export one — walking to the
/// key window's root view controller (and down through anything already presented on top of it) is
/// the same thing every iOS sample does by hand.
/// </summary>
internal static class CurrentViewController
{
    public static UIViewController? Get()
    {
        var scene = UIApplication.SharedApplication.ConnectedScenes
            .OfType<UIWindowScene>()
            .FirstOrDefault(s => s.ActivationState == UISceneActivationState.ForegroundActive);

        var window = scene?.Windows.FirstOrDefault(w => w.IsKeyWindow);
        var root = window?.RootViewController;

        while (root?.PresentedViewController is { } presented) root = presented;

        return root;
    }
}

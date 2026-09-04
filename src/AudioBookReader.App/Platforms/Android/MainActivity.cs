using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidUri = Android.Net.Uri;

namespace AudioBookReader.App;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private const int PickDocumentRequest = 0x_B0_0C;

    private static TaskCompletionSource<AndroidUri?>? _pending;

    /// <summary>
    /// Shows the system document picker and returns what was chosen, as a lasting reference.
    ///
    /// Done here rather than through the cross-platform picker because that one copies the chosen
    /// file into the cache before returning it — half a gigabyte copied for nothing, and the
    /// original hidden behind a cache path that will be swept away. Asking the system directly
    /// keeps the document, and the persistable flag keeps permission to read it after a reboot.
    /// </summary>
    public static Task<AndroidUri?> PickDocumentAsync(string prompt)
    {
        // A picker already waiting is abandoned rather than left to hang forever on a result that
        // will now be delivered to its replacement.
        _pending?.TrySetResult(null);
        _pending = new TaskCompletionSource<AndroidUri?>();

        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);

        // Everything, on purpose: the type reported for .m4b and .epub varies by device and by
        // storage provider, and filtering is what greyed out the very files this app opens.
        intent.SetType("*/*");

        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantPersistableUriPermission);

        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity is null)
        {
            _pending.TrySetResult(null);
            return _pending.Task;
        }

        // Started directly, not through a chooser: the system document picker is the only handler
        // for this action, and wrapping it loses the persistable grant.
        activity.StartActivityForResult(intent, PickDocumentRequest);
        return _pending.Task;
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        if (requestCode != PickDocumentRequest) return;

        var pending = _pending;
        _pending = null;

        pending?.TrySetResult(resultCode == Result.Ok ? data?.Data : null);
    }
}

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AudioBookReader.App.Services;
using AndroidUri = Android.Net.Uri;

namespace AudioBookReader.App;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation)]
// The television home screen lists only activities in this category; the ordinary launcher entry
// that MainLauncher adds is invisible there.
[IntentFilter([Intent.ActionMain], Categories = [Intent.CategoryLeanbackLauncher])]
public class MainActivity : MauiAppCompatActivity
{
    /// <summary>Set by the alignment notification, to say which book it was reporting on.</summary>
    public const string ExtraShowBook = "showBook";

    private const int PickDocumentRequest = 0x_B0_0C;
    private const int PickFolderRequest = 0x_B0_0D;
    private const int PickImageRequest = 0x_B0_0E;

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

        // A television has no system document picker, but a file manager installed on it often
        // answers the older "get content" request — which is how a book on a USB stick gets in.
        // What that hands back carries no lasting permission, so the importer copies the file in
        // instead of referencing it, which is what it already does with anything it cannot hold.
        if (!HasRealHandler(activity, intent))
        {
            intent = new Intent(Intent.ActionGetContent);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("*/*");
            intent.AddFlags(ActivityFlags.GrantReadUriPermission);
        }

        // Started directly, not through a chooser: the system document picker is the only handler
        // for this action, and wrapping it loses the persistable grant.
        StartPicker(activity, intent, PickDocumentRequest);
        return _pending.Task;
    }

    /// <summary>
    /// Whether anything real answers the request.
    ///
    /// Asked of the package manager, which sees these requests because the manifest declares them
    /// in its queries. A television can answer with a placeholder — Sony routes the folder picker
    /// to a stub that only says "you don't have an app that can do this" — so that counts as no
    /// answer at all.
    /// </summary>
    private static bool HasRealHandler(Activity activity, Intent intent)
    {
        if (activity.PackageManager is not { } packages) return true;

        var handler = intent.ResolveActivity(packages);
        return handler is not null && handler.PackageName?.Contains("packagestubs", StringComparison.Ordinal) != true;
    }

    /// <summary>
    /// Opens a picker, or says plainly that there is none.
    ///
    /// Televisions usually ship without one, and starting an activity nothing handles throws — out
    /// of a button's command, which closes the app. Answered instead as "nothing chosen", with a
    /// note pointing at the ways that do work on such a device.
    /// </summary>
    private static void StartPicker(Activity activity, Intent intent, int request)
    {
        try
        {
            if (!HasRealHandler(activity, intent)) throw new ActivityNotFoundException();

            activity.StartActivityForResult(intent, request);
        }
        catch (ActivityNotFoundException)
        {
            AppLog.Info($"no picker for {intent.Action} on this device");

            _pending?.TrySetResult(null);

            // A dialog rather than a toast: the note is two sentences of instructions, and a toast
            // cut it off after the first line and was gone before anyone across a room had read it.
            Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(async () =>
            {
                try
                {
                    await Dialogs.ShowAsync(
                        null,
                        AudioBookReader.App.Resources.Strings.Strings.Picker_Unavailable,
                        AudioBookReader.App.Resources.Strings.Strings.Common_Close);
                }
                catch (Exception ex)
                {
                    AppLog.Error("explaining there is no picker", ex);
                }
            });
        }
    }

    /// <summary>
    /// Asks for a folder and keeps the right to write in it.
    ///
    /// A whole tree rather than one document, because the app writes into it repeatedly — a book
    /// downloaded today and another next month, both landing beside the ones already there. The
    /// persistable grant is what makes that survive a reboot; without it the folder would have to be
    /// chosen again every time.
    /// </summary>
    /// <param name="startAt">
    /// Where to open the picker. The system will not grant a folder without someone confirming it,
    /// but it will start them in the right place — which turns "go and find Audiobooks" into one tap.
    /// </param>
    public static Task<AndroidUri?> PickFolderAsync(AndroidUri? startAt = null)
    {
        _pending?.TrySetResult(null);
        _pending = new TaskCompletionSource<AndroidUri?>();

        var intent = new Intent(Intent.ActionOpenDocumentTree);

        if (startAt is not null && Build.VERSION.SdkInt >= BuildVersionCodes.O)
            intent.PutExtra(Android.Provider.DocumentsContract.ExtraInitialUri, startAt);

        intent.AddFlags(ActivityFlags.GrantReadUriPermission
                        | ActivityFlags.GrantWriteUriPermission
                        | ActivityFlags.GrantPersistableUriPermission);

        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity is null)
        {
            _pending.TrySetResult(null);
            return _pending.Task;
        }

        StartPicker(activity, intent, PickFolderRequest);
        return _pending.Task;
    }

    /// <summary>
    /// Shows the phone's own photo gallery and returns the picture chosen.
    ///
    /// The system photo picker, not the document picker the two above use. Those are for keeping a
    /// lasting reference to a book, and they look like a file manager because that is what they
    /// are; this is for finding a photograph among photographs, where thumbnails are the whole
    /// point. It also asks for nothing: the photo picker hands back one image with a read grant of
    /// its own, which is exactly why it exists and why this app needs no permission to see photos.
    ///
    /// Before Android 13 there is no such picker, so the document picker stands in, narrowed to
    /// images — which at least lists the gallery among its sources.
    /// </summary>
    public static Task<AndroidUri?> PickImageAsync()
    {
        _pending?.TrySetResult(null);
        _pending = new TaskCompletionSource<AndroidUri?>();

        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity is null)
        {
            _pending.TrySetResult(null);
            return _pending.Task;
        }

        Intent intent;

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            intent = new Intent(Android.Provider.MediaStore.ActionPickImages);
            intent.SetType("image/*");
        }
        else
        {
            intent = new Intent(Intent.ActionOpenDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("image/*");
        }

        // No persistable flag, unlike a book: the picture is read once, turned into text and
        // forgotten, so there is nothing to come back to after a reboot.
        StartPicker(activity, intent, PickImageRequest);
        return _pending.Task;
    }

    /// <summary>Readies the screen for a remote before an arrow or OK is acted on.</summary>
    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e is { Action: KeyEventActions.Down } && RemoteFocus.IsNavigationKey(e.KeyCode))
            RemoteFocus.PrepareForKeys(this);

        return base.DispatchKeyEvent(e);
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Show(Intent);
    }

    /// <summary>
    /// Arrives when the app is already running, which is the usual case: the notification is only
    /// there while alignment runs, and alignment usually runs because someone just started it.
    /// </summary>
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);

        Intent = intent;
        Show(intent);
    }

    /// <summary>
    /// Opens the book the intent names, once there is a shell to open it in.
    ///
    /// Posted rather than called: OnCreate runs before MAUI has built anything, so navigating here
    /// would be navigating a shell that does not exist yet.
    /// </summary>
    private static void Show(Intent? intent)
    {
        var bookId = intent?.GetIntExtra(ExtraShowBook, -1) ?? -1;
        if (bookId < 0) return;

        // Cleared so that a rotation, or the activity being rebuilt, does not reopen the book
        // every time from an intent that has already been acted on.
        intent!.RemoveExtra(ExtraShowBook);

        Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                // A short wait for the shell on a cold start. Nothing to hook: the app is built
                // after the activity, and there is no event for "the shell exists now".
                for (var i = 0; Shell.Current is null && i < 40; i++) await Task.Delay(100);

                if (Shell.Current is null) return;

                await Shell.Current.GoToAsync($"details?id={bookId}");
            }
            catch (Exception ex)
            {
                AppLog.Error("opening the book from its notification", ex);
            }
        });
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        if (requestCode is not (PickDocumentRequest or PickFolderRequest or PickImageRequest)) return;

        var pending = _pending;
        _pending = null;

        pending?.TrySetResult(resultCode == Result.Ok ? data?.Data : null);
    }
}

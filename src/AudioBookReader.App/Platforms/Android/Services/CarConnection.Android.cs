using Android.Content;
using AndroidUri = Android.Net.Uri;

namespace AudioBookReader.App.Services;

/// <summary>
/// Asks Android Auto directly, through the content provider it publishes for exactly this question.
///
/// Queried rather than taken from a library: androidx.car.app exists to answer it, but pulling in
/// the whole car-app dependency for one integer is not worth it when the provider behind it is a
/// documented, stable address. Nothing is cached — a drive can start or end at any moment, and the
/// question is only ever asked when a book is about to be opened.
/// </summary>
public partial class CarConnection
{
    private const string Authority = "androidx.car.app.connection";
    private const string StateColumn = "CarConnectionState";

    /// <summary>Not connected, so the phone is on its own.</summary>
    private const int NotConnected = 0;

    public partial bool IsConnected
    {
        get
        {
            try
            {
                var uri = AndroidUri.Parse($"content://{Authority}/carconnection");
                if (uri is null) return false;

                using var cursor = global::Android.App.Application.Context.ContentResolver?
                    .Query(uri, null, null, null, null);

                if (cursor is null || !cursor.MoveToNext()) return false;

                var column = cursor.GetColumnIndex(StateColumn);
                return column >= 0 && cursor.GetInt(column) != NotConnected;
            }
            catch (Exception ex)
            {
                // A phone without Android Auto has no such provider, which is not a failure — it is
                // an answer. Anything else here is equally not worth refusing to open a book over.
                AppLog.Info($"car connection unknown ({ex.GetType().Name}), assuming no car");
                return false;
            }
        }
    }
}

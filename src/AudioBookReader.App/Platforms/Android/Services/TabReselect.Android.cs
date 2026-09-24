using Android.Views;
using Google.Android.Material.Navigation;

namespace AudioBookReader.App.Services;

public partial class TabReselect
{
    /// <summary>
    /// The bar currently hooked, so the same one is not subscribed to twice.
    ///
    /// Held weakly in effect by comparing references only: the bar dies with the shell, and holding
    /// it would keep a whole destroyed view hierarchy alive for the life of the app.
    /// </summary>
    private NavigationBarView? _bar;

    public partial void Watch()
    {
        // A convenience, and called from a page's OnAppearing, where anything thrown reaches the
        // runtime and closes the app. Losing the reselect gesture for a session is the right
        // price for any failure here.
        try
        {
            WatchCore();
        }
        catch (Exception ex)
        {
            AppLog.Error("watching the tab bar", ex);
        }
    }

    private void WatchCore()
    {
        var activity = global::Android.App.Application.Context as global::Android.App.Activity
                       ?? Platform.CurrentActivity;

        if (activity?.Window?.DecorView is not ViewGroup root) return;

        var bar = Find(root);
        if (bar is null || ReferenceEquals(bar, _bar)) return;

        // The shell is rebuilt on every language change, which means a new bar every time. Without
        // this, the old one — and everything its own subscription chain keeps reachable — was never
        // let go, so a session that switched languages a few times left that many dead bars pinned
        // in memory for no reason.
        if (_bar is not null)
        {
            try
            {
                _bar.ItemReselected -= OnReselected;
            }
            catch (ObjectDisposedException)
            {
                // Closing the app with back ends the activity but not the process, so on the next
                // launch this still pointed at the old activity's bar, already torn down —
                // unsubscribing from it threw, and every such reopen crashed. There is nothing
                // left to let go of.
            }
        }

        _bar = bar;
        bar.ItemReselected += OnReselected;
    }

    private void OnReselected(object? sender, NavigationBarView.ItemReselectedEventArgs e)
    {
        // The bar knows which item was tapped, not which route it stands for, and matching by
        // title would break the moment the app changed language. The tab's position is the one
        // thing both sides agree on.
        if (Shell.Current?.CurrentItem is not { } tabs) return;

        var index = IndexOf(_bar, e.P0.ItemId);
        if (index < 0 || index >= tabs.Items.Count) return;

        if (tabs.Items[index].CurrentItem?.Route is { Length: > 0 } route) Raise(route);
    }

    /// <summary>Where in the bar an item sits, which is where its tab sits in the shell.</summary>
    private static int IndexOf(NavigationBarView? bar, int itemId)
    {
        if (bar?.Menu is not { } menu) return -1;

        for (var i = 0; i < menu.Size(); i++)
            if (menu.GetItem(i)?.ItemId == itemId) return i;

        return -1;
    }

    /// <summary>
    /// The bottom bar somewhere under this view.
    ///
    /// Found by walking rather than asked for, because Shell builds it inside its own renderer and
    /// exposes no handle to it. A depth-first walk over a view tree this shallow costs nothing, and
    /// it runs once per shell rather than once per frame.
    /// </summary>
    private static NavigationBarView? Find(ViewGroup group)
    {
        for (var i = 0; i < group.ChildCount; i++)
        {
            var child = group.GetChildAt(i);

            if (child is NavigationBarView bar) return bar;
            if (child is ViewGroup nested && Find(nested) is { } found) return found;
        }

        return null;
    }
}

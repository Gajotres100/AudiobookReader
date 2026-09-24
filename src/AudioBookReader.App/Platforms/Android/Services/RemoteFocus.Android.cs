using System.Reflection;
using AColor = Android.Graphics.Color;
using Android.Graphics.Drawables;
using Android.Views;
using Microsoft.Maui.Handlers;
using AView = Android.Views.View;
using MView = Microsoft.Maui.Controls.View;

namespace AudioBookReader.App.Services;

public static partial class RemoteFocus
{
    public static partial void Register()
    {
        LayoutHandler.Mapper.AppendToMapping(nameof(RemoteFocus), (handler, view) => MakeTappable(handler.PlatformView, view));
        BorderHandler.Mapper.AppendToMapping(nameof(RemoteFocus), (handler, view) => MakeTappable(handler.PlatformView, view));
        ContentViewHandler.Mapper.AppendToMapping(nameof(RemoteFocus), (handler, view) => MakeTappable(handler.PlatformView, view));

        // A tappable piece of text or picture: the player's chapter title, which opens the chapter
        // list, is a label — unreachable from a remote until labels were included here.
        LabelHandler.Mapper.AppendToMapping(nameof(RemoteFocus), (handler, view) => MakeTappable(handler.PlatformView, view));
        ImageHandler.Mapper.AppendToMapping(nameof(RemoteFocus), (handler, view) => MakeTappable(handler.PlatformView, view));

        ButtonHandler.Mapper.AppendToMapping(nameof(RemoteFocus), (handler, _) => Ring(handler.PlatformView));
        ImageButtonHandler.Mapper.AppendToMapping(nameof(RemoteFocus), (handler, _) => Ring(handler.PlatformView));
        SwitchHandler.Mapper.AppendToMapping(nameof(RemoteFocus), (handler, _) => Ring(handler.PlatformView));
    }

    /// <summary>
    /// Stops lists from swallowing focus, before a remote's key is acted on.
    ///
    /// A list takes focus itself by default — and so does the pager Shell keeps its pages in, which
    /// fills the whole screen and cannot be reached through any handler. Focused, it shows nothing,
    /// and the arrows went nowhere: the buttons inside it are not "above" or "beside" a view that
    /// already covers them. Letting only what is inside a list take focus sends the arrows to the
    /// rows and buttons themselves. Done on a key press rather than on every layout, so a phone that
    /// is only ever touched never pays for the walk.
    /// </summary>
    public static void PrepareForKeys(global::Android.App.Activity activity)
    {
        if (activity.Window?.DecorView is not ViewGroup root) return;

        Walk(root);

        static void Walk(ViewGroup group)
        {
            if (group is AndroidX.RecyclerView.Widget.RecyclerView list)
            {
                if (list.Focusable)
                {
                    list.DescendantFocusability = DescendantFocusability.AfterDescendants;
                    list.FocusableInTouchMode = false;
                    list.Focusable = false;
                }

                // Rows bound later — scrolled into view by the very key being handled — are fixed as
                // they arrive, rather than on the key after.
                if (!Watched.TryGetValue(list, out _))
                {
                    Watched.Add(list, new object());
                    list.AddOnChildAttachStateChangeListener(new RowWatcher());
                }

                for (var i = 0; i < list.ChildCount; i++) PassFocusInward(list.GetChildAt(i));
            }

            // A scrolling area takes focus too, and holds on to the arrows to scroll with. That is
            // right for a long text with nothing to click in it, and wrong everywhere else: on a
            // book's download page the cover's scroll area took focus on the way down and the
            // buttons beneath it could not be reached. So it keeps focus only when it genuinely
            // has somewhere to scroll and nothing inside that could take focus instead.
            if (group is AndroidX.Core.Widget.NestedScrollView or global::Android.Widget.ScrollView
                && group.Focusable)
            {
                var content = group.ChildCount > 0 ? group.GetChildAt(0) : null;
                // With a margin: a cover a few pixels taller than its frame is not something anyone
                // needs to scroll through, and it was enough to keep the arrows from getting past it.
                var canScroll = content is not null && content.Height > group.Height * 1.2;

                if (!canScroll || HasFocusableInside(group))
                {
                    group.DescendantFocusability = DescendantFocusability.AfterDescendants;
                    group.Focusable = false;
                }
            }

            for (var i = 0; i < group.ChildCount; i++)
                if (group.GetChildAt(i) is ViewGroup child) Walk(child);
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AndroidX.RecyclerView.Widget.RecyclerView, object> Watched = new();

    /// <summary>
    /// Lets a list row's own content take focus instead of the row's wrapper.
    ///
    /// Every row sits in a wrapper that is clickable and focusable, for selection — which none of
    /// these lists use. Focus went to the wrapper, which draws no ring and knows nothing of OK, so
    /// a book cover could be reached and seen to do nothing. Only when something inside can take
    /// focus: a row with nothing tappable keeps its wrapper, so it is still reachable.
    /// </summary>
    private static void PassFocusInward(AView? row)
    {
        if (row is not ViewGroup wrapper || !wrapper.Focusable || !HasFocusableInside(wrapper)) return;

        wrapper.DescendantFocusability = DescendantFocusability.AfterDescendants;
        wrapper.Focusable = false;
    }

    private static bool HasFocusableInside(ViewGroup group)
    {
        for (var i = 0; i < group.ChildCount; i++)
        {
            var child = group.GetChildAt(i);
            if (child is null || child.Visibility != ViewStates.Visible) continue;
            if (child.Focusable) return true;
            if (child is ViewGroup nested && HasFocusableInside(nested)) return true;
        }

        return false;
    }

    private sealed class RowWatcher : Java.Lang.Object, AndroidX.RecyclerView.Widget.RecyclerView.IOnChildAttachStateChangeListener
    {
        public void OnChildViewAttachedToWindow(AView view) => PassFocusInward(view);

        public void OnChildViewDetachedFromWindow(AView view) { }
    }

    public static bool IsNavigationKey(Keycode key) =>
        key is Keycode.DpadUp or Keycode.DpadDown or Keycode.DpadLeft or Keycode.DpadRight
            or Keycode.DpadCenter or Keycode.Enter or Keycode.Tab;

    private static void MakeTappable(AView? platform, IView view)
    {
        if (platform is null || view is not MView element) return;

        var taps = element.GestureRecognizers.OfType<TapGestureRecognizer>().ToList();
        if (taps.Count == 0) return;

        platform.Focusable = true;
        Ring(platform);

        // One listener per view, replaced rather than added to, since the mapper runs again
        // whenever the view is re-bound — a recycled row in a list is the usual case.
        platform.SetOnKeyListener(new OkIsTap(element));
    }

    /// <summary>Draws a ring over the view while it has focus, and nothing otherwise.</summary>
    private static void Ring(AView? platform)
    {
        if (platform is null || !OperatingSystem.IsAndroidVersionAtLeast(23)) return;

        var density = platform.Resources?.DisplayMetrics?.Density ?? 2f;

        // Two strokes, dark outside and light inside, so the ring shows up on the cream page, on
        // the dark theme and on a brass button alike — one colour vanishes against one of them.
        var outer = new GradientDrawable();
        outer.SetCornerRadius(12 * density);
        outer.SetStroke((int)(3 * density), AColor.ParseColor("#1F1A14"));

        var inner = new GradientDrawable();
        inner.SetCornerRadius(10 * density);
        inner.SetStroke((int)(2 * density), AColor.ParseColor("#FFC266"));

        var ring = new LayerDrawable([outer, inner]);
        var inset = (int)(3 * density);
        ring.SetLayerInset(1, inset, inset, inset, inset);

        var states = new StateListDrawable();
        states.AddState([global::Android.Resource.Attribute.StateFocused], ring);
        states.AddState([], new ColorDrawable(AColor.Transparent));

        platform.Foreground = states;

        // A layout with no background of its own is marked as drawing nothing, and Android then
        // skips its draw pass altogether — foreground included — and only draws its children.
        // Buttons showed the ring; book covers, which are layouts, did not.
        if (platform is ViewGroup group) group.SetWillNotDraw(false);
    }

    /// <summary>OK, Enter or a gamepad's A button does what a tap on the view would.</summary>
    private sealed class OkIsTap(MView element) : Java.Lang.Object, AView.IOnKeyListener
    {
        public bool OnKey(AView? v, Keycode keyCode, KeyEvent? e)
        {
            if (keyCode is not (Keycode.DpadCenter or Keycode.Enter or Keycode.NumpadEnter or Keycode.ButtonA))
                return false;

            // Acted on when the key comes up, like a click; the key going down is claimed too, so
            // nothing underneath acts on the same press.
            if (e?.Action == KeyEventActions.Up) Tap(element);
            return true;
        }
    }

    private static void Tap(MView element)
    {
        foreach (var tap in element.GestureRecognizers.OfType<TapGestureRecognizer>())
        {
            try
            {
                if (SendTapped(tap, element)) continue;

                // The command alone, should the event route ever be unavailable.
                if (tap.Command?.CanExecute(tap.CommandParameter) == true) tap.Command.Execute(tap.CommandParameter);
            }
            catch (Exception ex)
            {
                AppLog.Error("acting on OK from the remote", ex);
            }
        }
    }

    /// <summary>
    /// Raises the recognizer as a real tap would — its Tapped event and its command both.
    ///
    /// MAUI keeps the method that does this internal, so it is found by name. A tap raised from
    /// outside the gesture system is exactly what it was not written to expect, but it is also the
    /// only way to reach handlers written as Tapped events rather than commands.
    /// </summary>
    private static bool SendTapped(TapGestureRecognizer tap, MView element)
    {
        _sendTapped ??= typeof(TapGestureRecognizer)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "SendTapped"
                                 && m.GetParameters() is { Length: >= 1 } p
                                 && p[0].ParameterType.IsAssignableFrom(typeof(MView)));

        if (_sendTapped is null) return false;

        var arguments = new object?[_sendTapped.GetParameters().Length];
        arguments[0] = element;

        _sendTapped.Invoke(tap, arguments);
        return true;
    }

    private static MethodInfo? _sendTapped;
}

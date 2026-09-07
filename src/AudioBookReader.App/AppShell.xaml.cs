using AudioBookReader.App.Services;
using AudioBookReader.App.Views;

namespace AudioBookReader.App;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();

		// Tapping the tab you are already on is not a navigation, so Shell never mentions it —
		// and it is the exact gesture behind "take me back to the list". The platform hears it;
		// this is how it gets up here.
		if (IPlatformApplication.Current?.Services.GetService<TabReselect>() is { } taps)
			taps.Reselected += (_, route) => ToRootOf(route);

		// Pages reached by navigation rather than from the shell's own structure have to be
		// registered by route, or GoToAsync cannot resolve them.
		Routing.RegisterRoute("book", typeof(BookPage));
		Routing.RegisterRoute("details", typeof(BookDetailsPage));
		Routing.RegisterRoute("reader", typeof(ReaderPage));
		Routing.RegisterRoute("bookmarks", typeof(BookmarksPage));
		Routing.RegisterRoute("server", typeof(ServerPage));
		Routing.RegisterRoute("serverbook", typeof(ServerBookPage));
	}

	/// <summary>
	/// Tapping a tab returns it to its own first screen.
	///
	/// Shell keeps a navigation stack per tab, so leaving the library from inside a book and coming
	/// back later reopened that book — the shelf was somewhere behind it, reachable only by pressing
	/// back. A tab is a place, not a history: going to it should show what it is, and the way back
	/// to a book is the book, which is right there on the shelf.
	///
	/// The double slash is what does it: an absolute route resets the stack instead of pushing onto
	/// it. Guarded on there being something to pop, so an ordinary tab switch does not navigate.
	/// </summary>
	protected override void OnNavigated(ShellNavigatedEventArgs args)
	{
		base.OnNavigated(args);

		if (CurrentItem?.CurrentItem is not { } section) return;
		if (section.Navigation.NavigationStack.Count <= 1) return;

		// Only when the tab itself was the destination. A push within a tab arrives here too, and
		// unwinding that would make every book page close the instant it opened.
		if (args.Source is not (ShellNavigationSource.ShellSectionChanged
			or ShellNavigationSource.ShellItemChanged
			or ShellNavigationSource.ShellContentChanged))
		{
			return;
		}

		// The route lives on the ShellContent, not on the section around it. Writing ShellContent
		// straight inside a TabBar has MAUI wrap it in a section of its own, and that wrapper gets
		// a generated route which resolves to nothing.
		if (section.CurrentItem?.Route is { Length: > 0 } route) ToRootOf(route);
	}

	/// <summary>
	/// Sends a tab back to its own first screen.
	///
	/// The double slash is what does it: an absolute route resets the stack instead of pushing onto
	/// it. Dispatched rather than awaited because both callers are handlers that must return before
	/// the navigation they are asking for can run.
	/// </summary>
	private void ToRootOf(string route) => Dispatcher.Dispatch(async () =>
	{
		try
		{
			await GoToAsync($"//{route}");
		}
		catch (Exception ex)
		{
			// Losing the reset leaves the old page showing, which is the previous behaviour rather
			// than a broken one.
			AppLog.Error("returning a tab to its first screen", ex);
		}
	});

	/// <summary>
	/// Back from a secondary tab returns to the first one; only from the first tab does it leave
	/// the app.
	///
	/// Without this, back out of Settings quits outright, which reads as a crash rather than as
	/// navigation — and it is the behaviour every other Android app has trained people to expect.
	/// </summary>
	protected override bool OnBackButtonPressed()
	{
		if (CurrentItem is { } tabs && tabs.Items.IndexOf(tabs.CurrentItem) > 0)
		{
			tabs.CurrentItem = tabs.Items[0];
			return true;
		}

		return base.OnBackButtonPressed();
	}
}

using AudioBookReader.App.Services;
using AudioBookReader.App.Views;

namespace AudioBookReader.App;

public partial class AppShell : Shell
{
	/// <summary>
	/// Whether the app-startup jump to the server has already happened this process.
	///
	/// A language change rebuilds this whole shell with <c>new AppShell()</c>, and that must not
	/// look like the app starting again — someone who switched languages while reading is not
	/// asking to be dropped onto the server shelf. Static and per-process rather than per-instance
	/// is what tells "the app just launched" apart from "the shell was just rebuilt".
	/// </summary>
	private static bool _openedServerOnStart;

	public AppShell()
	{
		InitializeComponent();

		// Tapping the tab you are already on is not a navigation, so Shell never mentions it —
		// and it is the exact gesture behind "take me back to the list". The platform hears it;
		// this is how it gets up here.
		if (IPlatformApplication.Current?.Services.GetService<TabReselect>() is { } taps)
			taps.Reselected += (_, route) => ToRootOf(route);

		// Deferred rather than run inline: the shell has no navigation stack to send anywhere
		// until it has actually finished appearing, and posting it is what lets that happen first.
		if (!_openedServerOnStart
			&& IPlatformApplication.Current?.Services.GetService<ServerConnections>() is
				{ IsConfigured: true, OpenServerOnStart: true })
		{
			_openedServerOnStart = true;
			Dispatcher.Dispatch(async () =>
			{
				try
				{
					// A slow or unreachable server must not hold the library tab hostage — someone
					// who opens the app to read on a plane, with the server unreachable, still ends
					// up looking at their books rather than a blank shelf that never loads. Racing
					// the connection against a wait rather than passing it a token: RestoreAsync has
					// no cancellation of its own to thread through, and abandoning the wait here is
					// what the person actually cares about, not whether the request itself gives up.
					if (await ServerReachableInTimeAsync()) await GoToAsync("server");
				}
				catch (Exception ex)
				{
					AppLog.Error("opening the server shelf on start", ex);
				}
			});
		}

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
	/// Whether the server answers within a wait worth making someone sit through on app start.
	///
	/// Ten seconds either restores a working sign-in or it does not; nothing about a server that
	/// takes longer than that to answer a request is going to feel like the app opened quickly.
	/// </summary>
	private static async Task<bool> ServerReachableInTimeAsync()
	{
		if (IPlatformApplication.Current?.Services.GetService<ServerConnections>()?.Active is not { } server)
			return false;

		var attempt = server.RestoreAsync();
		var timedOut = await Task.WhenAny(attempt, Task.Delay(TimeSpan.FromSeconds(10))) != attempt;

		return !timedOut && await attempt;
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

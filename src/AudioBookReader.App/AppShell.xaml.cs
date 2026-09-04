using AudioBookReader.App.Views;

namespace AudioBookReader.App;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();

		// Pages reached by navigation rather than from the shell's own structure have to be
		// registered by route, or GoToAsync cannot resolve them.
		Routing.RegisterRoute("book", typeof(BookPage));
		Routing.RegisterRoute("reader", typeof(ReaderPage));
		Routing.RegisterRoute("bookmarks", typeof(BookmarksPage));
	}

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

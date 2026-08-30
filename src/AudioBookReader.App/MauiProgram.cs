using AudioBookReader.App.Services;
using AudioBookReader.App.ViewModels;
using AudioBookReader.App.Views;
using AudioBookReader.Core.Books;
using AudioBookReader.Core.Data;
using Microsoft.Extensions.Logging;

namespace AudioBookReader.App;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		AppPaths.EnsureCreated();

		// Single instances: the database holds one connection, and the sync map store and model
		// store are stateless wrappers over a directory. The foreground services resolve these
		// through IPlatformApplication rather than being constructed with them, since Android
		// creates the service objects itself.
		builder.Services.AddSingleton(_ => new LibraryDatabase(AppPaths.LibraryDatabase));
		builder.Services.AddSingleton(_ => new SyncMapStore(AppPaths.SyncMaps));
		builder.Services.AddSingleton(_ => new WhisperModelStore(AppPaths.Models));
		builder.Services.AddSingleton<BookTextExtractors>();
		builder.Services.AddSingleton<LibraryService>();
		builder.Services.AddSingleton<AlignmentQueue>();
		builder.Services.AddSingleton<BookImporter>();
		builder.Services.AddSingleton<AlignmentSettingsStore>();
		builder.Services.AddSingleton<BookFilePicker>();

		// Playback outlives any page, so the controller is shared; pages and their view models are
		// created fresh each time they are navigated to.
		builder.Services.AddSingleton<PlaybackController>();

		builder.Services.AddTransient<LibraryViewModel>();
		builder.Services.AddTransient<LibraryPage>();
		builder.Services.AddTransient<BookViewModel>();
		builder.Services.AddTransient<BookPage>();
		builder.Services.AddTransient<ReaderViewModel>();
		builder.Services.AddTransient<ReaderPage>();
		builder.Services.AddTransient<SettingsViewModel>();
		builder.Services.AddTransient<SettingsPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		// Nothing is blocked on here: the database creates its schema on first use, so startup
		// never waits on I/O from the UI thread.
		return builder.Build();
	}
}

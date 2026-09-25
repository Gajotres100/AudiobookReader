using AudioBookReader.App.Services;
using AudioBookReader.App.ViewModels;
using AudioBookReader.App.Views;
using AudioBookReader.Core.Books;
using AudioBookReader.Core.Data;
using Microsoft.Extensions.Logging;
using Plugin.Maui.OCR;

namespace AudioBookReader.App;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();

		// Arrow keys and OK reach everything a finger can, for televisions and their remotes.
		RemoteFocus.Register();
		builder
			.UseMauiApp<App>()
			.UseOcr()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		AppPaths.EnsureCreated();

		// First of all, so that everything after it is recorded. The system log is a shared ring
		// buffer a few megabytes wide: alignment runs for hours, and a failure reported the next
		// morning has long since scrolled out of it.
		AppLog.File = AppPaths.Log;

		// Before anything is built, because every label asks for its text as it is created.
		new Language().Apply();

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
		builder.Services.AddSingleton<DownloadQueue>();
		builder.Services.AddSingleton<TabReselect>();
		builder.Services.AddSingleton<WordTranslator>();
		builder.Services.AddSingleton<MediaReferences>();
		builder.Services.AddSingleton<CarConnection>();
		builder.Services.AddSingleton<LiveSyncRunner>();
		builder.Services.AddSingleton<BookImporter>();
		builder.Services.AddSingleton<StorageSweep>();
		builder.Services.AddSingleton<ProgressSync>();
		builder.Services.AddSingleton<AlignmentShare>();
		builder.Services.AddSingleton<AlignmentSettingsStore>();
		builder.Services.AddSingleton<BookFilePicker>();
		builder.Services.AddSingleton<PhotoPicker>();
		builder.Services.AddSingleton<ServerAccount>();
		builder.Services.AddSingleton<DownloadFolder>();
		builder.Services.AddSingleton<Language>();
		builder.Services.AddSingleton<ServerConnection>();

		// Playback outlives any page, so the controller is shared; pages and their view models are
		// created fresh each time they are navigated to.
		builder.Services.AddSingleton<PlaybackController>();

		builder.Services.AddTransient<LibraryViewModel>();
		builder.Services.AddTransient<LibraryPage>();
		builder.Services.AddTransient<BookViewModel>();
		builder.Services.AddTransient<BookPage>();
		builder.Services.AddTransient<BookDetailsPage>();
		builder.Services.AddTransient<ReaderViewModel>();
		builder.Services.AddTransient<ReaderPage>();
		builder.Services.AddTransient<BookmarksViewModel>();
		builder.Services.AddTransient<BookmarksPage>();
		builder.Services.AddTransient<SettingsViewModel>();
		builder.Services.AddTransient<SettingsPage>();
		builder.Services.AddTransient<ServerViewModel>();
		builder.Services.AddTransient<ServerPage>();
		builder.Services.AddTransient<ServerBookViewModel>();
		builder.Services.AddTransient<ServerBookPage>();
		builder.Services.AddTransient<DownloadDestinationPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		// The stored choice has to reach AppLog before anything logs, and the settings page may
		// never be opened in a session that needs the detail.
		AppLog.Verbose = new AlignmentSettingsStore().VerboseLog;

		// Nothing is blocked on here: the database creates its schema on first use, so startup
		// never waits on I/O from the UI thread.
		return builder.Build();
	}
}

using Microsoft.Extensions.DependencyInjection;

namespace AudioBookReader.App;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		// The player and the file references reach books played from the server through this.
		Services.StreamedAudio.Source = activationState?.Context.Services.GetService<Services.ServerConnection>();

		// Clears out files an interrupted import left behind, once the app has settled. Off the UI
		// thread and a few seconds late, so it never slows the first screen.
		if (activationState?.Context.Services.GetService<Services.StorageSweep>() is { } sweep)
			_ = Task.Run(async () =>
			{
				await Task.Delay(TimeSpan.FromSeconds(5));
				await sweep.RunAsync();
			});

		return new Window(new AppShell());
	}
}
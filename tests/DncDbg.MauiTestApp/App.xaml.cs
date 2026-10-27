using Microsoft.Extensions.DependencyInjection;

namespace DncDbg.MauiTestApp;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new AppShell());
#if DEBUG && DNCDBG_EXPERIMENTAL_REMOTE
		window.Destroying += async (_, _) => await ExperimentalStartup.StopAsync();
#endif
		return window;
	}
}
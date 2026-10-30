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
#if DEBUG
		window.Destroying += (_, _) => TargetAgentStartup.Stop();
#endif
		return window;
	}
}
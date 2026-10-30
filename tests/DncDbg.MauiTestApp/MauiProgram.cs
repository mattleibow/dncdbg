using Microsoft.Extensions.Logging;
#if DEBUG
using Microsoft.Maui.DevFlow.Agent;
#endif

namespace DncDbg.MauiTestApp;

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

#if DEBUG
		builder.Logging.AddDebug();
		if (!TargetAgentStartup.ConsumeDebuggerOnlyMode())
			builder.AddMauiDevFlowAgent();
		TargetAgentStartup.Start();
#endif

		return builder.Build();
	}
}

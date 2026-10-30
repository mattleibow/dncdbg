#if MACOS
using Microsoft.Maui.Platforms.MacOS.Essentials;
using Microsoft.Maui.Platforms.MacOS.Hosting;
#endif
#if DEBUG
using Microsoft.Maui.DevFlow.Agent;
#endif

namespace DncDbg.MixedMode.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        MauiAppBuilder builder = MauiApp.CreateBuilder();

#if MACOS
        builder
            .UseMauiAppMacOS<App>()
            .AddMacOSEssentials();
#else
        builder.UseMauiApp<App>();
#endif

#if DEBUG
        builder.AddMauiDevFlowAgent();
#endif

        return builder.Build();
    }
}

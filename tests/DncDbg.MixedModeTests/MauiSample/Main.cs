#if MACOS
using AppKit;
using Foundation;
using Microsoft.Maui.Platforms.MacOS.Platform;
#elif MACCATALYST
using Foundation;
using UIKit;
#endif

namespace DncDbg.MixedMode.Maui;

#if MACOS
public static class MainClass
{
    public static void Main(string[] args)
    {
        NSApplication.Init();
        NSApplication.SharedApplication.Delegate = new AppDelegate();
        NSApplication.Main(args);
    }
}

[Register("DncDbgMixedModeAppDelegate")]
public sealed class AppDelegate : MacOSMauiApplication
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
#elif MACCATALYST
public static class MainClass
{
    public static void Main(string[] args) =>
        UIApplication.Main(args, null, typeof(AppDelegate));
}

[Register(nameof(AppDelegate))]
public sealed class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}

[Register(nameof(SceneDelegate))]
public sealed class SceneDelegate : MauiUISceneDelegate
{
}
#endif

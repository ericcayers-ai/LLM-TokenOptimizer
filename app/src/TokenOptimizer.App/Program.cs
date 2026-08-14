using Avalonia;
using System;
using TokenOptimizer.Core.Diagnostics;

namespace TokenOptimizer.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Re-sync PATH from the registry before anything else runs, so a
        // tool installed moments ago (by hand, or by this app's own
        // winget/pip calls in a PRIOR run) is visible without a reboot.
        PathRefresher.Refresh();
        LaunchArgs.Parse(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}

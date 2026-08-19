using Avalonia;
using System;
using System.Linq;
using System.Threading.Tasks;
using TokenOptimizer.App.Cli;
using TokenOptimizer.Core.Diagnostics;

namespace TokenOptimizer.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // Re-sync PATH from the registry before anything else runs, so a
        // tool installed moments ago (by hand, or by this app's own
        // winget/pip calls in a PRIOR run) is visible without a reboot.
        PathRefresher.Refresh();

        // Headless command surface: `TokenOptimizer.App.exe --cli <command>
        // [options]`. Every feature the VS Code extension needs goes through
        // here instead of shelling out to the legacy PowerShell script - one
        // JSON object on stdout, no window/display server required. Must be
        // checked before LaunchArgs.Parse/Avalonia startup, neither of which
        // this path touches.
        if (args.Length > 0 && args[0] == "--cli")
        {
            return CliHost.RunAsync(args.Skip(1).ToArray()).GetAwaiter().GetResult();
        }

        LaunchArgs.Parse(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

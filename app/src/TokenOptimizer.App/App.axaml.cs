using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using TokenOptimizer.App.ViewModels;
using TokenOptimizer.App.Views;

namespace TokenOptimizer.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

#if DEBUG
        // Modern AvaloniaUI Developer Tools bridge (F12). The old
        // AppBuilder.WithDeveloperTools() API was removed in Avalonia 12;
        // AvaloniaUI.DiagnosticsSupport now attaches from the Application.
        this.AttachDeveloperTools();
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Microsoft.Extensions.DependencyInjection;
using XivVault.Desktop.Services;
using XivVault.Desktop.ViewModels;
using XivVault.Desktop.Views;

namespace XivVault.Desktop;

public partial class App : Application
{
    /// <summary>Lets the screenshot tool and UI tests supply their own services (fake profile, fake processes).</summary>
    internal static Func<IServiceProvider>? ServicesOverride { get; set; }

    public IServiceProvider Services { get; private set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Services = ServicesOverride?.Invoke() ?? new ServiceCollection().AddXivVaultDesktop().BuildServiceProvider();

        // Motion is opt-out: with Windows animations off, the animation styles are never loaded.
        if (!Services.GetRequiredService<IMotionSettings>().ReduceMotion)
        {
            Styles.Add(new StyleInclude(new Uri("avares://XivVault/App.axaml")) { Source = new Uri("avares://XivVault/Themes/Motion.axaml") });
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = CreateMainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Switches to another set of services (another fake PC) and opens a window on it.</summary>
    internal MainWindow CreateMainWindow(IServiceProvider services)
    {
        Services = services;
        return CreateMainWindow();
    }

    public MainWindow CreateMainWindow()
    {
        var viewModel = Services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = viewModel };
        Services.GetRequiredService<WindowServices>().TopLevel = window;
        _ = viewModel.InitializeAsync();
        return window;
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XivVault.Core;
using XivVault.Core.Logging;
using XivVault.Core.Platform;
using XivVault.Desktop.Services;
using XivVault.Desktop.ViewModels;

namespace XivVault.Desktop;

public static class DesktopServices
{
    /// <summary>Core plus the desktop's view models and UI services. Tests replace the UI services first.</summary>
    public static IServiceCollection AddXivVaultDesktop(this IServiceCollection services)
    {
        services.AddXivVaultCore();
        services.AddSingleton<ILoggerProvider>(provider =>
            new FileLoggerProvider(Path.Combine(provider.GetRequiredService<IAppEnvironment>().DataDirectory, "logs")));

        services.AddSingleton<Navigator>();
        services.AddSingleton<INavigator>(provider => provider.GetRequiredService<Navigator>());
        services.AddSingleton<OverlayDialogService>();
        services.AddSingleton<IDialogService>(provider => provider.GetRequiredService<OverlayDialogService>());
        services.AddSingleton<WindowServices>();
        services.AddSingleton<IClipboardService>(provider => provider.GetRequiredService<WindowServices>());
        services.AddSingleton<IFilePicker>(provider => provider.GetRequiredService<WindowServices>());
        services.AddSingleton<IShellService, WindowsShellService>();
        services.AddSingleton<IUiThread, AvaloniaUiThread>();
        services.AddSingleton<IMotionSettings, WindowsMotionSettings>();
        services.AddSingleton<ISchedulerTarget, DesktopSchedulerTarget>();

        services.AddSingleton<DesktopSession>();
        services.AddSingleton<OverviewViewModel>();
        services.AddSingleton<BackupsViewModel>();
        services.AddSingleton<RestoreViewModel>();
        services.AddSingleton<ScheduleViewModel>();
        services.AddSingleton<DiagnosticsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<PageViewModel>(provider => provider.GetRequiredService<OverviewViewModel>());
        services.AddSingleton<PageViewModel>(provider => provider.GetRequiredService<BackupsViewModel>());
        services.AddSingleton<PageViewModel>(provider => provider.GetRequiredService<RestoreViewModel>());
        services.AddSingleton<PageViewModel>(provider => provider.GetRequiredService<ScheduleViewModel>());
        services.AddSingleton<PageViewModel>(provider => provider.GetRequiredService<DiagnosticsViewModel>());
        services.AddSingleton<PageViewModel>(provider => provider.GetRequiredService<SettingsViewModel>());
        services.AddSingleton<MainWindowViewModel>();
        return services;
    }
}

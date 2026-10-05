using System.Runtime.Versioning;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Locators;
using XivVault.Core.Scheduling;
using XivVault.Desktop.Services;

namespace XivVault.Desktop;

internal static class Program
{
    [STAThread]
    [SupportedOSPlatform("windows")]
    public static int Main(string[] args)
    {
        // Setup and the uninstaller start the app with their own arguments; Velopack handles those
        // and exits. A downloaded update is only installed when the user chooses it in Settings, so
        // a scheduled backup never starts by replacing the program it runs from.
        if (VelopackUpdater.IsInstalledWithSetup())
        {
            VelopackApp.Build()
                .SetAutoApplyOnStartup(false)
                .OnBeforeUninstallFastCallback(_ => RemoveScheduleBeforeUninstall())
                .Run();
        }

        // The scheduled task runs the app with this argument: back up with no window, then exit.
        if (args.Contains(DesktopSchedulerTarget.ScheduledBackupArgument, StringComparer.OrdinalIgnoreCase))
        {
            return RunScheduledBackup();
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static int RunScheduledBackup()
    {
        using var services = new ServiceCollection().AddXivVaultDesktop().BuildServiceProvider();
        var outcome = services.GetRequiredService<ScheduledBackupRunner>().RunAsync().GetAwaiter().GetResult();
        return outcome.Result == Core.State.ScheduledRunResult.Success ? 0 : (int)(outcome.Error ?? Core.XivVaultErrorKind.Unexpected);
    }

    private static void RemoveScheduleBeforeUninstall()
    {
        if (VelopackLocator.Current.RootAppDir is not { } installFolder)
        {
            return;
        }

        using var services = new ServiceCollection().AddXivVaultDesktop().BuildServiceProvider();
        try
        {
            services.GetRequiredService<UninstallCleanup>().RunAsync(installFolder).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Uninstalling must not fail over this; Diagnostics reports a task whose program is gone.
            services.GetRequiredService<ILogger<UninstallCleanup>>().LogError(ex, "Could not remove the scheduled backup task during uninstall");
        }
    }
}

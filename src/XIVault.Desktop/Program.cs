using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using XIVault.Core.Scheduling;
using XIVault.Desktop.Services;

namespace XIVault.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
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
        using var services = new ServiceCollection().AddXivaultDesktop().BuildServiceProvider();
        var outcome = services.GetRequiredService<ScheduledBackupRunner>().RunAsync().GetAwaiter().GetResult();
        return outcome.Result == Core.State.ScheduledRunResult.Success ? 0 : (int)(outcome.Error ?? Core.XivaultErrorKind.Unexpected);
    }
}

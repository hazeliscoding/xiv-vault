using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using XivVault.Core;
using XivVault.Desktop;
using XivVault.Desktop.Services;
using XivVault.Desktop.ViewModels;

namespace XivVault.Screenshots;

internal static class Program
{
    private static string _output = "";

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHarfBuzz()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--demo")
        {
            return RecordDemo(args.ElementAtOrDefault(1) ?? "demo.gif", args.ElementAtOrDefault(2));
        }

        _output = Path.GetFullPath(args.FirstOrDefault() ?? "screenshots");
        Directory.CreateDirectory(_output);
        // An optional second argument picks the fake profile folder; paths in the screenshots show it.
        var root = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(Path.GetTempPath(), "xiv-vault-screens-" + Guid.NewGuid().ToString("N")[..8]);
        using var world = World.Create(root, withHistory: true);
        using var fresh = World.Create(root + "-fresh", withHistory: false);

        App.ServicesOverride = () => world.Services;
        BuildAvaloniaApp().SetupWithoutStarting();

        var window = Open(world);
        var main = (MainWindowViewModel)window.DataContext!;
        var services = world.Services;

        Shot(window, "overview");
        Go(main, AppPage.Backups);
        Shot(window, "backups");
        var backups = services.GetRequiredService<BackupsViewModel>();
        backups.Rows[0].IsExpanded = true;
        Shot(window, "backups-inspect");
        backups.Rows[0].IsExpanded = false;
        Run(backups.DeleteCommand.ExecuteAsync(backups.Rows[1]), untilReady: () => services.GetRequiredService<OverlayDialogService>().Active is not null);
        Shot(window, "backups-delete");
        services.GetRequiredService<OverlayDialogService>().Active?.Close(false);

        Go(main, AppPage.Restore);
        Shot(window, "restore-1-choose");
        var restore = services.GetRequiredService<RestoreViewModel>();
        Run(restore.ContinueCommand.ExecuteAsync(null));
        Shot(window, "restore-2-review");
        restore.TogglePluginListCommand.Execute(null);
        restore.ChooseNoPluginsCommand.Execute(null);
        foreach (var name in new[] { "Splatoon", "Pixel Perfect" })
        {
            restore.PluginChoices.Single(choice => choice.Name == name).IsChosen = true;
        }

        restore.DalamudSettingsChosen = false;
        Shot(window, "restore-2-choose");
        restore.ChooseAllPluginsCommand.Execute(null);
        restore.DalamudSettingsChosen = true;
        restore.TogglePluginListCommand.Execute(null);
        Run(restore.ContinueCommand.ExecuteAsync(null));
        Shot(window, "restore-3-checks");
        Run(restore.RestoreNowCommand.ExecuteAsync(null));
        Shot(window, "restore-4-complete");

        Go(main, AppPage.Schedule);
        Shot(window, "schedule");
        Go(main, AppPage.Diagnostics);
        Shot(window, "diagnostics");
        Go(main, AppPage.Settings);
        Shot(window, "settings");
        services.GetRequiredService<SettingsViewModel>().ToggleOverrideCommand.Execute(null);
        Shot(window, "settings-override");
        services.GetRequiredService<SettingsViewModel>().ToggleOverrideCommand.Execute(null);
        // Always one minor version ahead, so the offer never names a version older than the app.
        var current = Version.Parse(XivVaultInfo.Version.Split('-')[0]);
        world.Updater.Latest = $"{current.Major}.{current.Minor + 1}.0";
        Run(services.GetRequiredService<UpdatesViewModel>().CheckNowCommand.ExecuteAsync(null));
        Shot(window, "settings-update");
        window.Close();

        var freshWindow = Open(fresh);
        Shot(freshWindow, "overview-empty");
        freshWindow.Close();

        // A new PC whose backup folder synced with Files On-Demand: every archive is still in the cloud.
        using var newPc = World.Create(root + "-new-pc", withHistory: true);
        newPc.MoveBackupsToCloud();
        App.ServicesOverride = () => newPc.Services;
        var cloudWindow = Open(newPc);
        var cloudMain = (MainWindowViewModel)cloudWindow.DataContext!;
        Shot(cloudWindow, "overview-cloud");
        Go(cloudMain, AppPage.Backups);
        Shot(cloudWindow, "backups-cloud");
        Go(cloudMain, AppPage.Restore);
        Shot(cloudWindow, "restore-1-cloud");
        Run(newPc.Services.GetRequiredService<RestoreViewModel>().ContinueCommand.ExecuteAsync(null));
        Shot(cloudWindow, "restore-2-cloud-review");
        cloudWindow.Close();

        Console.WriteLine($"Screenshots written to {_output}");
        return 0;
    }

    /// <summary>
    /// Records the README's tour with animations on: a backup, the backup list, then a restore.
    /// Needs ffmpeg on PATH.
    /// </summary>
    private static int RecordDemo(string gif, string? profile)
    {
        var root = profile is not null ? Path.GetFullPath(profile) : Path.Combine(Path.GetTempPath(), "xiv-vault-demo-" + Guid.NewGuid().ToString("N")[..8]);
        using var world = World.Create(root, withHistory: true, demo: true);
        App.ServicesOverride = () => world.Services;
        BuildAvaloniaApp().SetupWithoutStarting();

        var window = Open(world);
        var main = (MainWindowViewModel)window.DataContext!;
        var services = world.Services;
        var frames = Directory.CreateTempSubdirectory("xiv-vault-demo-frames-");
        try
        {
            var recorder = new Recorder(window, frames.FullName, framesPerSecond: 15);
            recorder.For(1.2);
            recorder.While(services.GetRequiredService<OverviewViewModel>().BackUpNowCommand.ExecuteAsync(null), 1.8);
            recorder.While(main.ShowAsync(AppPage.Backups, null), 0.9);
            services.GetRequiredService<BackupsViewModel>().Rows[0].IsExpanded = true;
            recorder.For(1.6);
            var restore = services.GetRequiredService<RestoreViewModel>();
            recorder.While(main.ShowAsync(AppPage.Restore, null), 1.0);
            recorder.While(restore.ContinueCommand.ExecuteAsync(null), 1.8);
            recorder.While(restore.ContinueCommand.ExecuteAsync(null), 1.4);
            recorder.While(restore.RestoreNowCommand.ExecuteAsync(null), 2.2);
            window.Close();
            recorder.WriteGif(gif, width: 1100);
        }
        finally
        {
            frames.Delete(recursive: true);
        }

        return 0;
    }

    private static Window Open(World world)
    {
        var window = ((App)Application.Current!).CreateMainWindow(world.Services);
        window.Width = 1100;
        window.Height = 720;
        window.Show();
        Settle(TimeSpan.FromSeconds(3));
        return window;
    }

    private static void Go(MainWindowViewModel main, AppPage page)
    {
        Run(main.ShowAsync(page, null));
        Settle(TimeSpan.FromSeconds(1));
    }

    private static void Run(Task task, Func<bool>? untilReady = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!(untilReady?.Invoke() ?? task.IsCompleted) && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Settle(TimeSpan.FromMilliseconds(400));
    }

    private static void Settle(TimeSpan time)
    {
        var until = DateTime.UtcNow + time;
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(15);
        }
    }

    private static void Shot(Window window, string name)
    {
        Settle(TimeSpan.FromMilliseconds(300));
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame was rendered.");
#pragma warning disable CS0618 // The replacement overload needs encoder options this tool has no use for.
        frame.Save(Path.Combine(_output, name + ".png"));
#pragma warning restore CS0618
        Console.WriteLine($"  {name}.png");
    }
}

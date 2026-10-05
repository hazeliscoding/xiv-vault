using XivVault.Core.Configuration;
using XivVault.Core.Platform;
using XivVault.Core.Scheduling;
using XivVault.Desktop.Controls;
using XivVault.Desktop.Services;
using XivVault.Desktop.ViewModels;

namespace XivVault.Tests.Desktop;

public class UpdatesViewModelTests
{
    [Fact]
    public async Task A_portable_copy_never_goes_online_and_links_to_releases()
    {
        using var host = new DesktopTestHost();
        host.Updater.IsInstalled = false;
        var updates = host.Get<UpdatesViewModel>();

        await updates.CheckOnStartupAsync();
        updates.OpenReleasesCommand.Execute(null);

        Assert.Equal(0, host.Updater.Checks);
        Assert.Equal(UpdateState.Portable, updates.State);
        Assert.False(updates.CheckNowCommand.CanExecute(null));
        Assert.Equal(["https://github.com/hazeliscoding/xiv-vault/releases"], host.Shell.Opened);
    }

    [Fact]
    public async Task The_startup_check_is_skipped_when_turned_off()
    {
        using var host = new DesktopTestHost();
        var store = host.Get<IConfigStore>();
        store.Save(store.Load() with { CheckForUpdates = false });
        var updates = host.Get<UpdatesViewModel>();

        await updates.CheckOnStartupAsync();

        Assert.Equal(0, host.Updater.Checks);
        Assert.Equal(UpdateState.NotChecked, updates.State);
    }

    [Fact]
    public async Task The_startup_check_is_skipped_when_the_settings_are_unreadable()
    {
        using var host = new DesktopTestHost();
        var store = host.Get<IConfigStore>();
        Directory.CreateDirectory(host.Environment.DataDirectory);
        File.WriteAllText(store.ConfigPath, "{ not json");
        var updates = host.Get<UpdatesViewModel>();

        await updates.CheckOnStartupAsync();

        Assert.Equal(0, host.Updater.Checks);
        Assert.Equal(UpdateState.NotChecked, updates.State);
    }

    [Fact]
    public async Task The_startup_check_finds_a_new_version_without_installing_it()
    {
        using var host = new DesktopTestHost();
        host.Updater.Latest = "0.2.0";
        var updates = host.Get<UpdatesViewModel>();

        await updates.CheckOnStartupAsync();

        Assert.Equal(UpdateState.Available, updates.State);
        Assert.True(updates.HasUpdate);
        Assert.Equal("Version 0.2.0 available", updates.NavLabel);
        Assert.Equal(Tone.Accent, updates.BadgeTone);
        Assert.Empty(host.Updater.Downloaded);
        Assert.Null(host.Updater.RestartedInto);
    }

    [Fact]
    public async Task Up_to_date_and_failed_checks_are_reported_quietly()
    {
        using var host = new DesktopTestHost();
        var updates = host.Get<UpdatesViewModel>();

        await updates.CheckNowCommand.ExecuteAsync(null);
        Assert.Equal(UpdateState.UpToDate, updates.State);
        Assert.Equal("Up to date", updates.BadgeLabel);

        host.Updater.CheckError = new HttpRequestException("No such host is known.");
        await updates.CheckNowCommand.ExecuteAsync(null);
        Assert.Equal(UpdateState.CheckFailed, updates.State);
        Assert.Equal(Tone.Warning, updates.BadgeTone);
        Assert.DoesNotContain("No such host", updates.Detail);
    }

    [Fact]
    public async Task Update_and_restart_downloads_then_restarts_into_the_new_version()
    {
        using var host = new DesktopTestHost();
        host.Updater.Latest = "0.2.0";
        var updates = host.Get<UpdatesViewModel>();
        await updates.CheckNowCommand.ExecuteAsync(null);

        await updates.UpdateAndRestartCommand.ExecuteAsync(null);

        Assert.Equal(["0.2.0"], host.Updater.Downloaded);
        Assert.Equal("0.2.0", host.Updater.RestartedInto);
        Assert.Null(updates.Notice);
    }

    [Fact]
    public async Task Update_waits_while_this_window_is_backing_up()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        host.Updater.Latest = "0.2.0";
        var updates = host.Get<UpdatesViewModel>();
        await updates.CheckNowCommand.ExecuteAsync(null);
        host.Session.IsRestoring = true;

        await updates.UpdateAndRestartCommand.ExecuteAsync(null);

        AssertWaited(host, updates, "backup or restore to finish");
    }

    [Fact]
    public async Task Update_waits_while_another_process_holds_the_operation_lock()
    {
        using var host = new DesktopTestHost();
        host.Updater.Latest = "0.2.0";
        var updates = host.Get<UpdatesViewModel>();
        await updates.CheckNowCommand.ExecuteAsync(null);

        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using (host.Get<OperationLock>().Acquire())
            {
                held.Set();
                release.Wait();
            }
        });
        holder.Start();
        held.Wait(TestContext.Current.CancellationToken);
        try
        {
            await updates.UpdateAndRestartCommand.ExecuteAsync(null);
        }
        finally
        {
            release.Set();
            holder.Join();
        }

        AssertWaited(host, updates, "A backup or restore is running");
    }

    [Fact]
    public async Task Update_waits_while_a_scheduled_backup_is_running()
    {
        using var host = new DesktopTestHost();
        host.Updater.Latest = "0.2.0";
        var updates = host.Get<UpdatesViewModel>();
        await updates.CheckNowCommand.ExecuteAsync(null);
        host.Instances.OthersRunning = true;

        await updates.UpdateAndRestartCommand.ExecuteAsync(null);

        AssertWaited(host, updates, "scheduled backup");
    }

    [Fact]
    public async Task Update_checks_again_after_downloading()
    {
        using var host = new DesktopTestHost();
        host.Updater.Latest = "0.2.0";
        host.Updater.DuringDownload = () => host.Instances.OthersRunning = true;
        var updates = host.Get<UpdatesViewModel>();
        await updates.CheckNowCommand.ExecuteAsync(null);

        await updates.UpdateAndRestartCommand.ExecuteAsync(null);

        Assert.Equal(["0.2.0"], host.Updater.Downloaded);
        Assert.Null(host.Updater.RestartedInto);
        Assert.Equal(UpdateState.Available, updates.State);
        Assert.Contains("scheduled backup", updates.Notice);
    }

    [Fact]
    public async Task A_failed_download_keeps_the_update_on_offer()
    {
        using var host = new DesktopTestHost();
        host.Updater.Latest = "0.2.0";
        host.Updater.DownloadError = new HttpRequestException("Connection reset");
        var updates = host.Get<UpdatesViewModel>();
        await updates.CheckNowCommand.ExecuteAsync(null);

        await updates.UpdateAndRestartCommand.ExecuteAsync(null);

        Assert.Null(host.Updater.RestartedInto);
        Assert.Equal(UpdateState.Available, updates.State);
        Assert.Contains("could not be downloaded", updates.Notice);
        Assert.True(updates.UpdateAndRestartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Turning_update_checks_off_in_Settings_is_saved()
    {
        using var host = new DesktopTestHost();
        var settings = host.Get<SettingsViewModel>();
        await settings.ActivateAsync();
        Assert.True(settings.CheckForUpdates);

        settings.CheckForUpdates = false;

        Assert.False(host.Get<IConfigStore>().Load().CheckForUpdates);
    }

    private static void AssertWaited(DesktopTestHost host, UpdatesViewModel updates, string reason)
    {
        Assert.Empty(host.Updater.Downloaded);
        Assert.Null(host.Updater.RestartedInto);
        Assert.Equal(UpdateState.Available, updates.State);
        Assert.Contains(reason, updates.Notice);
    }
}

public class UninstallCleanupTests
{
    private const string InstallFolder = @"C:\Users\roze\AppData\Local\XivVault";

    [Fact]
    public async Task Removes_the_task_that_starts_the_installed_copy_and_keeps_the_preferences()
    {
        using var host = new DesktopTestHost();
        var store = host.Get<IConfigStore>();
        var settings = new ScheduleSettings { Enabled = true, Frequency = ScheduleFrequency.Daily, Time = "21:00" };
        store.Save(store.Load() with { Schedule = settings });
        InstallTask(host, InstallFolder + @"\current\XIV-Vault.exe");

        await host.Get<UninstallCleanup>().RunAsync(InstallFolder, TestContext.Current.CancellationToken);

        Assert.Contains(host.Commands.Calls, call => call.Arguments[0] == "/Delete");
        var saved = store.Load().Schedule;
        Assert.False(saved.Enabled);
        Assert.Equal(ScheduleFrequency.Daily, saved.Frequency);
        Assert.Equal("21:00", saved.Time);
    }

    [Theory]
    [InlineData(@"C:\Tools\xiv-vault.exe")]
    [InlineData(@"C:\Users\roze\AppData\Local\XivVault Portable\XIV-Vault.exe")]
    public async Task Keeps_a_task_that_starts_another_copy(string executable)
    {
        using var host = new DesktopTestHost();
        InstallTask(host, executable);

        await host.Get<UninstallCleanup>().RunAsync(InstallFolder, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(host.Commands.Calls, call => call.Arguments[0] == "/Delete");
    }

    [Fact]
    public async Task Does_nothing_without_a_task()
    {
        using var host = new DesktopTestHost();

        await host.Get<UninstallCleanup>().RunAsync(InstallFolder, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(host.Commands.Calls, call => call.Arguments[0] == "/Delete");
    }

    private static void InstallTask(DesktopTestHost host, string executable)
    {
        var xml = TaskXml.Build(new ScheduleSettings { Enabled = true }, new ScheduledCommand(executable, "--scheduled-backup"), @"PC\roze", DateTime.Now);
        host.Commands.Handler = (_, args) => args[0] == "/Query" ? new CommandResult(0, xml, "") : new CommandResult(0, "", "");
    }
}

using System.Text;
using XivVault.Core.Configuration;
using XivVault.Core.Platform;
using XivVault.Core.Scheduling;
using XivVault.Core.State;
using XivVault.Tests.Support;

namespace XivVault.Tests.Core;

public class SchedulingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ScheduledCommand Command = new(@"C:\Apps\XIV Vault\xiv-vault.exe", "backup --scheduled");

    [Fact]
    public void Weekly_task_xml_round_trips()
    {
        var settings = new ScheduleSettings { Enabled = true, Frequency = ScheduleFrequency.Weekly, Days = [DayOfWeek.Sunday, DayOfWeek.Wednesday], Time = "18:30" };

        var xml = TaskXml.Build(settings, Command, @"PC\user", new DateTime(2026, 10, 4, 9, 0, 0));
        var (parsed, command) = TaskXml.Parse(xml);

        Assert.Contains("<StartBoundary>2026-10-04T18:30:00</StartBoundary>", xml);
        Assert.Contains("<LogonType>InteractiveToken</LogonType>", xml);
        Assert.Contains("<RunLevel>LeastPrivilege</RunLevel>", xml);
        Assert.Contains("<StartWhenAvailable>true</StartWhenAvailable>", xml);
        Assert.Equal(ScheduleFrequency.Weekly, parsed!.Frequency);
        Assert.Equal([DayOfWeek.Sunday, DayOfWeek.Wednesday], parsed.Days);
        Assert.Equal("18:30", parsed.Time);
        Assert.Equal(Command, command);
    }

    [Fact]
    public void Daily_and_logon_tasks_round_trip()
    {
        var daily = TaskXml.Parse(TaskXml.Build(new ScheduleSettings { Enabled = true, Frequency = ScheduleFrequency.Daily, Time = "06:00" }, Command, @"PC\user", DateTime.Now)).Settings!;
        var logon = TaskXml.Build(new ScheduleSettings { Enabled = true, Frequency = ScheduleFrequency.AtLogon }, Command, @"PC\user", DateTime.Now);

        Assert.Equal(ScheduleFrequency.Daily, daily.Frequency);
        Assert.Equal("06:00", daily.Time);
        Assert.Contains(@"<UserId>PC\user</UserId>", logon);
        Assert.Equal(ScheduleFrequency.AtLogon, TaskXml.Parse(logon).Settings!.Frequency);
    }

    [Fact]
    public void Arguments_with_special_characters_are_escaped()
    {
        var xml = TaskXml.Build(new ScheduleSettings { Enabled = true }, new ScheduledCommand(@"C:\R&D\xiv-vault.exe", "backup <x>"), @"PC\user", DateTime.Now);

        Assert.Contains("C:\\R&amp;D\\xiv-vault.exe", xml);
        Assert.Equal("backup <x>", TaskXml.Parse(xml).Command!.Arguments);
    }

    [Theory]
    [InlineData("2026-10-04T09:00", ScheduleFrequency.Daily, "12:00", "2026-10-04T12:00")]
    [InlineData("2026-10-04T13:00", ScheduleFrequency.Daily, "12:00", "2026-10-05T12:00")]
    [InlineData("2026-10-04T13:00", ScheduleFrequency.Weekly, "12:00", "2026-10-11T12:00")]
    [InlineData("2026-10-04T11:00", ScheduleFrequency.Weekly, "12:00", "2026-10-04T12:00")]
    public void Next_run_is_computed_from_the_schedule(string now, ScheduleFrequency frequency, string time, string expected)
    {
        var settings = new ScheduleSettings { Enabled = true, Frequency = frequency, Days = [DayOfWeek.Sunday], Time = time };

        Assert.Equal(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), NextRun.After(settings, DateTime.Parse(now, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Logon_schedules_have_no_clock_time()
    {
        Assert.Null(NextRun.After(new ScheduleSettings { Frequency = ScheduleFrequency.AtLogon }, DateTime.Now));
    }

    [Fact]
    public async Task Install_writes_utf16_xml_and_calls_schtasks()
    {
        using var host = new TestHost();
        string? xml = null;
        host.Commands.Handler = (_, args) =>
        {
            if (args[0] == "/Create")
            {
                var bytes = File.ReadAllBytes(args[4]);
                Assert.Equal(0xFF, bytes[0]);
                Assert.Equal(0xFE, bytes[1]);
                xml = Encoding.Unicode.GetString(bytes);
            }

            return new CommandResult(0, "", "");
        };
        var scheduler = host.Get<IBackupScheduler>();

        await scheduler.InstallAsync(new ScheduleSettings { Enabled = true }, Command, Ct);

        var call = Assert.Single(host.Commands.Calls);
        Assert.Equal("schtasks.exe", call.File);
        Assert.Equal(["/Create", "/TN", WindowsTaskScheduler.TaskName, "/XML"], call.Arguments.Take(4));
        Assert.Equal("/F", call.Arguments[^1]);
        Assert.Contains(@"<UserId>TESTPC\tester</UserId>", xml);
        Assert.False(File.Exists(call.Arguments[4]), "The temporary XML file is removed.");
    }

    [Fact]
    public async Task Status_reads_the_task_back_and_adds_the_last_recorded_run()
    {
        using var host = new TestHost();
        var settings = new ScheduleSettings { Enabled = true, Frequency = ScheduleFrequency.Weekly, Days = [DayOfWeek.Sunday], Time = "12:00" };
        host.Commands.Handler = (_, _) => new CommandResult(0, TaskXml.Build(settings, Command, "PC\\u", DateTime.Now), "");
        host.State.Update(state => state with { LastScheduledRun = new ScheduledRunRecord(new DateTime(2026, 9, 28, 18, 38, 0, DateTimeKind.Utc), ScheduledRunResult.Success, "Successful", "x.zip") });

        var status = await host.Get<IBackupScheduler>().GetStatusAsync(Ct);

        Assert.True(status.Installed);
        Assert.Equal(ScheduleFrequency.Weekly, status.InstalledSettings!.Frequency);
        Assert.Equal(new DateTime(2026, 10, 11, 12, 0, 0), status.NextRunLocal);
        Assert.Equal(ScheduledRunResult.Success, status.LastRun!.Result);
    }

    [Fact]
    public async Task A_missing_task_reports_not_installed()
    {
        using var host = new TestHost();
        host.Commands.Handler = (_, _) => new CommandResult(1, "", "ERROR: The system cannot find the file specified.");

        var status = await host.Get<IBackupScheduler>().GetStatusAsync(Ct);

        Assert.False(status.Installed);
        Assert.Null(status.Problem);
    }

    [Fact]
    public async Task Schedule_service_saves_preferences_and_removes_the_task_when_disabled()
    {
        using var host = new TestHost();
        var installed = false;
        host.Commands.Handler = (_, args) =>
        {
            switch (args[0])
            {
                case "/Create":
                    installed = true;
                    return new CommandResult(0, "", "");
                case "/Delete":
                    installed = false;
                    return new CommandResult(0, "", "");
                default:
                    return installed
                        ? new CommandResult(0, TaskXml.Build(new ScheduleSettings { Enabled = true }, Command, "PC\\u", DateTime.Now), "")
                        : new CommandResult(1, "", "not found");
            }
        };
        var service = host.Get<ScheduleService>();

        var on = await service.ApplyAsync(new ScheduleSettings { Enabled = true, Frequency = ScheduleFrequency.Daily, Time = "21:00" }, Command, Ct);
        Assert.True(on.Installed);
        Assert.True(host.Config.Load().Schedule.Enabled);

        var off = await service.DisableAsync(Ct);
        Assert.False(off.Installed);
        var saved = host.Config.Load().Schedule;
        Assert.False(saved.Enabled);
        Assert.Equal(ScheduleFrequency.Daily, saved.Frequency);
        Assert.Equal("21:00", saved.Time);
    }

    [Fact]
    public async Task Scheduled_run_backs_up_and_records_success()
    {
        using var host = new TestHost();
        host.CreateLauncher();

        var outcome = await host.Get<ScheduledBackupRunner>().RunAsync(cancellationToken: Ct);

        Assert.Equal(ScheduledRunResult.Success, outcome.Result);
        Assert.Equal(XivVault.Core.Backup.BackupKind.Scheduled, outcome.Backup!.Manifest.BackupType);
        Assert.Equal(ScheduledRunResult.Success, host.State.Load().LastScheduledRun!.Result);
    }

    [Fact]
    public async Task Scheduled_run_waits_for_the_game_then_gives_up()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.Processes.Running.Add("ffxiv_dx11");
        var options = new ScheduledRunOptions { PollInterval = TimeSpan.FromMilliseconds(5), MaxWait = TimeSpan.FromMilliseconds(20) };
        var runner = new ScheduledBackupRunner(
            host.Backups,
            host.Get<XivVault.Core.Restore.GameProcessGuard>(),
            host.State,
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ScheduledBackupRunner>.Instance);

        var outcome = await runner.RunAsync(options, Ct);

        Assert.Equal(ScheduledRunResult.Skipped, outcome.Result);
        Assert.Empty(host.Catalog.List(host.BackupFolder));
        Assert.Equal(ScheduledRunResult.Skipped, host.State.Load().LastScheduledRun!.Result);
    }

    [Fact]
    public async Task Scheduled_run_backs_up_once_the_game_closes()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.Processes.Running.Add("ffxiv_dx11");
        var options = new ScheduledRunOptions { PollInterval = TimeSpan.FromMilliseconds(5), MaxWait = TimeSpan.FromSeconds(30) };
        var runner = new ScheduledBackupRunner(
            host.Backups,
            host.Get<XivVault.Core.Restore.GameProcessGuard>(),
            host.State,
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ScheduledBackupRunner>.Instance);

        var run = runner.RunAsync(options, Ct);
        await Task.Delay(50, Ct);
        Assert.False(run.IsCompleted);
        host.Processes.Running.Clear();

        Assert.Equal(ScheduledRunResult.Success, (await run).Result);
    }

    [Fact]
    public async Task Scheduled_run_failure_is_recorded()
    {
        using var host = new TestHost();

        var outcome = await host.Get<ScheduledBackupRunner>().RunAsync(cancellationToken: Ct);

        Assert.Equal(ScheduledRunResult.Failed, outcome.Result);
        Assert.Equal(XivVault.Core.XivVaultErrorKind.XivLauncherNotFound, outcome.Error);
        Assert.Equal(ScheduledRunResult.Failed, host.State.Load().LastScheduledRun!.Result);
    }
}

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Testing;
using XivVault.Cli;
using XivVault.Core;
using XivVault.Core.Backup;
using XivVault.Core.Configuration;
using XivVault.Core.Platform;
using XivVault.Tests.Support;

namespace XivVault.Tests.Cli;

public class CliTests
{
    private static async Task<(int Code, string Output)> Run(TestHost host, params string[] args)
    {
        var console = new TestConsole();
        console.Profile.Width = 200;
        var code = await XivVaultCli.RunAsync(
            args,
            services =>
            {
                services.AddSingleton<IAppEnvironment>(host.Environment);
                services.AddSingleton<IProcessInspector>(host.Processes);
                services.AddSingleton<ICommandRunner>(host.Commands);
                services.AddSingleton<TimeProvider>(host.Clock);
                services.AddSingleton<IFileAvailability>(host.Files);
            },
            console,
            TestContext.Current.CancellationToken);
        return (code, console.Output);
    }

    [Fact]
    public async Task Backup_creates_an_archive_and_reports_it()
    {
        using var host = new TestHost();
        host.CreateLauncher();

        var (code, output) = await Run(host, "backup");

        Assert.Equal(0, code);
        Assert.Contains("Backed up 5 plugin configurations and Dalamud settings", output);
        Assert.Contains("xiv-vault-2026-10-04-131900.zip", output);
        Assert.Single(Directory.GetFiles(host.BackupFolder, "xiv-vault-*.zip"));
    }

    [Fact]
    public async Task Quiet_backup_prints_nothing()
    {
        using var host = new TestHost();
        host.CreateLauncher();

        var (code, output) = await Run(host, "backup", "--quiet");

        Assert.Equal(0, code);
        Assert.Equal("", output.Trim());
    }

    [Fact]
    public async Task Backup_to_another_destination()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var other = Path.Combine(host.Environment.Root, "Elsewhere");

        var (code, _) = await Run(host, "backup", "--destination", other);

        Assert.Equal(0, code);
        Assert.Single(Directory.GetFiles(other, "*.zip"));
    }

    [Fact]
    public async Task Missing_XIVLauncher_exits_with_3()
    {
        using var host = new TestHost();

        var (code, output) = await Run(host, "backup");

        Assert.Equal(3, code);
        Assert.Contains("XIVLauncher was not found", output);
    }

    [Fact]
    public async Task Unavailable_destination_exits_with_7()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var freeLetter = "QRSTUVWXYZ".First(letter => !Directory.Exists($"{letter}:\\"));

        var (code, _) = await Run(host, "backup", "--destination", $@"{freeLetter}:\XIV Vault");

        Assert.Equal(7, code);
    }

    [Fact]
    public async Task List_json_describes_each_backup()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        await host.BackUpAsync();

        var (code, output) = await Run(host, "list", "--json");

        Assert.Equal(0, code);
        var item = Assert.Single(JsonDocument.Parse(output).RootElement.EnumerateArray());
        Assert.Equal("manual", item.GetProperty("kind").GetString());
        Assert.Equal("verified", item.GetProperty("integrity").GetString());
        Assert.Equal(5, item.GetProperty("pluginConfigCount").GetInt32());
    }

    [Fact]
    public async Task List_shows_online_only_backups_without_downloading_them()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.AddCloudFile("xiv-vault-2026-10-03-080000.zip");

        var (code, output) = await Run(host, "list");

        Assert.Equal(0, code);
        Assert.Contains("In the cloud", output);
    }

    [Fact]
    public async Task List_verifies_only_the_latest_backup()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        await host.BackUpAsync();
        await host.BackUpAsync();
        host.State.Update(state =>
        {
            state.Verifications.Clear();
            return state;
        });

        var (code, _) = await Run(host, "list");

        Assert.Equal(0, code);
        var records = host.Catalog.List(host.BackupFolder);
        Assert.Equal(IntegrityState.Verified, records[0].Integrity);
        Assert.Equal(IntegrityState.Unverified, records[1].Integrity);
    }

    [Fact]
    public async Task List_verify_downloads_and_checks_every_backup()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        await host.BackUpAsync();
        host.AddCloudFile("xiv-vault-2026-10-03-080000.zip");

        var (code, output) = await Run(host, "list", "--verify");

        Assert.Equal(4, code);
        Assert.Contains("Failed", output);
    }

    [Fact]
    public async Task List_verify_reports_a_backup_it_could_not_download_without_marking_it_damaged()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var backup = await host.BackUpAsync();
        host.MoveToCloud(backup.Record.FilePath);

        int code;
        string output;
        using (new FileStream(backup.Record.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            (code, output) = await Run(host, "list", "--verify");
        }

        Assert.Equal(7, code);
        Assert.Contains("couldn't be downloaded", output);
        Assert.Equal(IntegrityState.Unverified, Assert.Single(host.Catalog.List(host.BackupFolder)).Integrity);
    }

    [Fact]
    public async Task List_json_marks_online_only_backups()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.AddCloudFile("xiv-vault-2026-10-03-080000.zip");

        var (code, output) = await Run(host, "list", "--json");

        Assert.Equal(0, code);
        var item = Assert.Single(JsonDocument.Parse(output).RootElement.EnumerateArray());
        Assert.True(item.GetProperty("onlineOnly").GetBoolean());
        Assert.Equal("unverified", item.GetProperty("integrity").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("pluginConfigCount").ValueKind);
    }

    [Fact]
    public async Task Status_json_reports_protection()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        await host.BackUpAsync();

        var (code, output) = await Run(host, "status", "--json");

        Assert.Equal(0, code);
        var root = JsonDocument.Parse(output).RootElement;
        Assert.Equal("protected", root.GetProperty("state").GetString());
        Assert.True(root.GetProperty("xivLauncher").GetProperty("found").GetBoolean());
        Assert.Equal(1, root.GetProperty("backupCount").GetInt32());
        Assert.False(root.GetProperty("schedule").GetProperty("installed").GetBoolean());
    }

    [Fact]
    public async Task Status_in_plain_text()
    {
        using var host = new TestHost();
        host.CreateLauncher();

        var (code, output) = await Run(host, "status");

        Assert.Equal(0, code);
        Assert.Contains("Not backed up yet", output);
        Assert.Contains("OneDrive / XIV Vault", output);
    }

    [Fact]
    public async Task Doctor_checks_all_areas_and_can_print_a_report()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        await host.BackUpAsync();

        var (code, json) = await Run(host, "doctor", "--json");
        var (_, report) = await Run(host, "doctor", "--report");

        Assert.Equal(0, code);
        Assert.Equal(4, JsonDocument.Parse(json).RootElement.GetProperty("groups").GetArrayLength());
        Assert.StartsWith("XIV Vault diagnostic report", report);
    }

    [Fact]
    public async Task Doctor_exits_with_3_when_XIVLauncher_is_missing()
    {
        using var host = new TestHost();

        var (code, _) = await Run(host, "doctor");

        Assert.Equal(3, code);
    }

    [Fact]
    public async Task Restore_latest_with_yes_restores_and_keeps_a_snapshot()
    {
        using var host = new TestHost();
        var launcher = host.CreateLauncher();
        await host.BackUpAsync();
        var original = launcher.ReadPluginConfig("Splatoon");
        launcher.WritePluginConfig("Splatoon", "changed");

        var (code, output) = await Run(host, "restore", "latest", "--yes");

        Assert.Equal(0, code);
        Assert.Contains("Restore complete", output);
        Assert.Equal(original, launcher.ReadPluginConfig("Splatoon"));
        Assert.Single(Directory.GetFiles(host.BackupFolder, "pre-restore-*.zip"));
    }

    [Fact]
    public async Task Restore_downloads_an_online_only_backup_first_and_says_how_big_it_is()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var backup = await host.BackUpAsync();
        host.MoveToCloud(backup.Record.FilePath);
        var size = Formatting.Bytes(new FileInfo(backup.Record.FilePath).Length);

        var (code, output) = await Run(host, "restore", "latest", "--yes");

        Assert.Equal(0, code);
        Assert.Contains($"This backup is in the cloud, so it downloads first ({size}).", output);
        Assert.Contains("Restore complete", output);
    }

    [Fact]
    public async Task Status_in_plain_text_counts_plugins_on_this_pc_while_the_latest_backup_is_in_the_cloud()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.AddCloudFile("xiv-vault-2026-10-04-080000.zip");

        var (code, output) = await Run(host, "status");

        Assert.Equal(0, code);
        Assert.Contains("in the cloud", output);
        Assert.Matches(@"Plugin configs\s+5", output);
    }

    [Fact]
    public async Task Restore_by_file_name()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var backup = await host.BackUpAsync();

        var (code, _) = await Run(host, "restore", backup.Record.FileName, "--yes");

        Assert.Equal(0, code);
    }

    [Fact]
    public async Task Restore_while_the_game_runs_exits_with_6()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        await host.BackUpAsync();
        host.Processes.Running.Add("ffxiv_dx11");

        var (code, output) = await Run(host, "restore", "latest", "--yes");

        Assert.Equal(6, code);
        Assert.Contains("Restore blocked", output);
    }

    [Fact]
    public async Task Restore_of_a_damaged_backup_exits_with_5()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var backup = await host.BackUpAsync();
        Core.RetentionTests.Corrupt(backup.Record.FilePath);

        var (code, _) = await Run(host, "restore", "latest", "--yes");

        Assert.Equal(5, code);
    }

    [Fact]
    public async Task Restore_without_yes_needs_a_person_to_confirm()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        await host.BackUpAsync();

        var (code, output) = await Run(host, "restore", "latest");

        Assert.Equal(2, code);
        Assert.Contains("--yes", output);
    }

    [Theory]
    [InlineData("backup", "--bogus")]
    [InlineData("frobnicate")]
    [InlineData("schedule", "weekly", "--day", "Funday")]
    [InlineData("schedule", "daily", "--time", "25:00")]
    [InlineData("config", "set", "retention", "0")]
    [InlineData("config", "set", "colour", "blue")]
    public async Task Bad_arguments_exit_with_2(params string[] args)
    {
        using var host = new TestHost();

        var (code, _) = await Run(host, args);

        Assert.Equal(2, code);
    }

    [Fact]
    public async Task Schedule_weekly_installs_the_task_and_saves_the_preference()
    {
        using var host = new TestHost();
        string? xml = null;
        host.Commands.Handler = (_, args) =>
        {
            if (args[0] == "/Create")
            {
                xml = File.ReadAllText(args[4]);
                return new(0, "", "");
            }

            return xml is null ? new(1, "", "not found") : new(0, xml, "");
        };

        var (code, output) = await Run(host, "schedule", "weekly", "--day", "Sunday", "--time", "18:30");

        Assert.Equal(0, code);
        Assert.Contains("Weekly on Sunday at 6:30 PM", output);
        Assert.Contains("<Sunday />", xml);
        Assert.Contains("backup --scheduled --quiet", xml);
        var saved = host.Config.Load().Schedule;
        Assert.True(saved.Enabled);
        Assert.Equal(ScheduleFrequency.Weekly, saved.Frequency);
        Assert.Equal("18:30", saved.Time);
    }

    [Fact]
    public async Task Schedule_remove_turns_automatic_backups_off()
    {
        using var host = new TestHost();
        host.UpdateConfig(config => config with { Schedule = config.Schedule with { Enabled = true } });

        var (code, _) = await Run(host, "schedule", "remove");

        Assert.Equal(0, code);
        Assert.False(host.Config.Load().Schedule.Enabled);
    }

    [Fact]
    public async Task Config_set_changes_settings()
    {
        using var host = new TestHost();

        Assert.Equal(0, (await Run(host, "config", "set", "retention", "15")).Code);
        Assert.Equal(0, (await Run(host, "config", "set", "include-ui", "yes")).Code);
        Assert.Equal(0, (await Run(host, "config", "set", "compression", "maximum")).Code);

        var config = host.Config.Load();
        Assert.Equal(15, config.RetentionCount);
        Assert.True(config.IncludeDalamudUi);
        Assert.Equal(CompressionPreset.Maximum, config.Compression);
    }

    [Fact]
    public async Task Version_prints_the_version()
    {
        using var host = new TestHost();

        var (code, output) = await Run(host, "version");

        Assert.Equal(0, code);
        Assert.Equal($"XIV Vault {XivVault.Core.XivVaultInfo.Version}", output.Trim());
    }
}

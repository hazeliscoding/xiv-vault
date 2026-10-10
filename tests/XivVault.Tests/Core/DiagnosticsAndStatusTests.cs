using XivVault.Core.Diagnostics;
using XivVault.Core.Platform;
using XivVault.Core.State;
using XivVault.Core.Status;
using XivVault.Tests.Support;

namespace XivVault.Tests.Core;

public class DiagnosticsAndStatusTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Status_moves_from_not_detected_to_protected()
    {
        using var host = new TestHost();
        var status = host.Get<IStatusService>();

        Assert.Equal(ProtectionState.NotDetected, (await status.GetAsync(cancellationToken: Ct)).State);

        host.CreateLauncher();
        var before = await status.GetAsync(cancellationToken: Ct);
        Assert.Equal(ProtectionState.NotBackedUp, before.State);
        Assert.Equal(5, before.Portable!.PluginConfigCount);

        await host.BackUpAsync();
        var after = await status.GetAsync(cancellationToken: Ct);
        Assert.Equal(ProtectionState.Protected, after.State);
        Assert.NotNull(after.LatestBackup);
    }

    [Fact]
    public async Task A_damaged_latest_backup_needs_attention()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var backup = await host.BackUpAsync();
        RetentionTests.Corrupt(backup.Record.FilePath);

        var status = await host.Get<IStatusService>().GetAsync(cancellationToken: Ct);

        Assert.Equal(ProtectionState.NeedsAttention, status.State);
        Assert.Contains("verification", status.AttentionReason);
    }

    [Fact]
    public async Task Safety_snapshots_do_not_count_as_the_latest_backup()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var backup = await host.BackUpAsync();
        await host.Restores.RestoreAsync(new(backup.Record.FilePath), cancellationToken: Ct);

        var status = await host.Get<IStatusService>().GetAsync(cancellationToken: Ct);

        Assert.Equal(backup.Record.FilePath, status.LatestBackup!.FilePath);
        Assert.Equal(2, status.Backups.Count);
    }

    [Fact]
    public async Task Diagnostics_cover_all_five_areas()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var install = Path.Combine(host.Environment.LocalAppData, "XIVLauncher");
        Directory.CreateDirectory(Path.Combine(install, "app-1.1.2"));
        File.WriteAllText(Path.Combine(install, "XIVLauncher.exe"), "stub");
        await host.BackUpAsync();

        var report = await host.Get<IDiagnosticsService>().RunAsync(Ct);

        Assert.Contains(report.Groups[0].Checks, check => check.Label == "XIVLauncher installed" && check.Detail == "v1.1.2");
        Assert.Equal(
            [DiagnosticArea.XivLauncher, DiagnosticArea.Dalamud, DiagnosticArea.BackupDestination, DiagnosticArea.Scheduling, DiagnosticArea.GameSettings],
            report.Groups.Select(group => group.Area));
        Assert.Equal(DiagnosticStatus.Healthy, report.Groups[0].Status);
        Assert.Equal(DiagnosticStatus.Healthy, report.Groups[1].Status);
        Assert.Contains(report.Groups[1].Checks, check => check.Label == "5 plugin configs detected");
        Assert.Contains(report.Groups.Single(group => group.Area == DiagnosticArea.BackupDestination).Checks, check => check.Label == "Latest backup verified");
        Assert.Contains(report.Groups.Single(group => group.Area == DiagnosticArea.Scheduling).Checks, check => check.Label == "Automatic backups are off" && check.Status == DiagnosticStatus.Warning);
    }

    [Fact]
    public async Task Missing_installation_is_an_error()
    {
        using var host = new TestHost();

        var report = await host.Get<IDiagnosticsService>().RunAsync(Ct);

        Assert.Equal(DiagnosticStatus.Error, report.Groups[0].Status);
        Assert.Equal(DiagnosticStatus.Error, report.Overall);
    }

    [Fact]
    public async Task Failed_scheduled_run_is_an_error()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.State.Update(state => state with { LastScheduledRun = new ScheduledRunRecord(DateTime.UtcNow, ScheduledRunResult.Failed, "The backup folder is not available.", null) });

        var report = await host.Get<IDiagnosticsService>().RunAsync(Ct);

        Assert.Contains(report.Groups.Single(group => group.Area == DiagnosticArea.Scheduling).Checks, check => check.Label == "Last run failed" && check.Status == DiagnosticStatus.Error);
    }

    [Fact]
    public async Task Report_text_never_contains_configuration_contents_or_the_profile_path()
    {
        using var host = new TestHost();
        var launcher = host.CreateLauncher();
        launcher.Write("""{ "SecretToken": "abc123-do-not-share" }""", "dalamudConfig.json");
        launcher.WritePluginConfig("Artisan", """{ "ApiKey": "plugin-secret-value" }""");
        host.UpdateConfig(config => config with { BackupDestination = Path.Combine(host.Environment.Root, "Backups") });
        await host.BackUpAsync();

        var text = (await host.Get<IDiagnosticsService>().RunAsync(Ct)).ToText();

        Assert.DoesNotContain("abc123-do-not-share", text);
        Assert.DoesNotContain("plugin-secret-value", text);
        Assert.DoesNotContain(host.Environment.UserProfile, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%AppData%\\XIVLauncher", text);
        Assert.Contains("XIVLauncher", text);
        Assert.Contains("Plugin configuration contents are never included.", text);
    }

    [Fact]
    public async Task Error_messages_that_quote_paths_are_redacted_in_the_report()
    {
        using var host = new TestHost();
        var missing = Path.Combine(host.Environment.UserProfile, "Games", "XIVLauncher");
        host.UpdateConfig(config => config with { XivLauncherPathOverride = missing });
        host.State.Update(state => state with
        {
            LastScheduledRun = new ScheduledRunRecord(
                DateTime.UtcNow,
                ScheduledRunResult.Failed,
                $"The backup folder {host.Environment.UserProfile}\\OneDrive\\XIV Vault is not available.",
                null),
        });

        var report = await host.Get<IDiagnosticsService>().RunAsync(Ct);
        var text = report.ToText();

        Assert.DoesNotContain(host.Environment.UserProfile, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"%USERPROFILE%\Games\XIVLauncher", text);
        Assert.Contains(@"%USERPROFILE%\OneDrive\XIV Vault", text);
    }

    [Fact]
    public void Paths_are_shown_the_way_people_know_them()
    {
        using var env = new TestEnvironment();
        var paths = new PathDisplay(env);

        Assert.Equal("OneDrive / XIV Vault", paths.Friendly(Path.Combine(env.OneDrive!, "XIV Vault")));
        Assert.Equal(@"%AppData%\XIVLauncher", paths.Friendly(env.XivLauncherPath));
        Assert.Equal(@"%USERPROFILE%\Documents\XIV Vault", paths.Redact(Path.Combine(env.Documents, "XIV Vault")));
        Assert.Equal(@"D:\XIV Vault", paths.Friendly(@"D:\XIV Vault"));
    }

    [Fact]
    public void Text_that_is_not_a_full_path_is_never_resolved_against_the_current_folder()
    {
        // The installed app runs from a folder inside the user profile. Redacting a friendly path
        // must not join it to that folder.
        var profile = Path.GetDirectoryName(Environment.CurrentDirectory)!;
        var paths = new PathDisplay(new ProfileAt(profile));

        Assert.Equal(@"%AppData%\XIVLauncher", paths.Redact(@"%AppData%\XIVLauncher"));
        Assert.Equal("OneDrive / XIV Vault", paths.Redact("OneDrive / XIV Vault"));
        Assert.Equal(@"%USERPROFILE%\Documents", paths.Redact(Path.Combine(profile, "Documents")));
    }

    private sealed class ProfileAt(string profile) : IAppEnvironment
    {
        public string RoamingAppData => Path.Combine(profile, "AppData", "Roaming");

        public string LocalAppData => Path.Combine(profile, "AppData", "Local");

        public string UserProfile => profile;

        public string Documents => Path.Combine(profile, "Documents");

        public string? OneDrive => null;

        public string TempPath => Path.GetTempPath();

        public string UserAccount => @"PC\user";

        public string DataDirectory => Path.Combine(LocalAppData, "XIV Vault");
    }
}

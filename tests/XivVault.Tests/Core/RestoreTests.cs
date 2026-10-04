using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using XivVault.Core;
using XivVault.Core.Backup;
using XivVault.Core.Configuration;
using XivVault.Core.Discovery;
using XivVault.Core.Platform;
using XivVault.Core.Restore;
using XivVault.Tests.Support;

namespace XivVault.Tests.Core;

public class RestoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Restores_portable_files_and_leaves_everything_else_alone()
    {
        using var host = new TestHost();
        var launcher = host.CreateLauncher();
        var backup = await host.BackUpAsync();
        var originalArtisan = launcher.ReadPluginConfig("Artisan");
        var originalConfig = launcher.Read("dalamudConfig.json");

        launcher.WritePluginConfig("Artisan", """{ "changed": true }""");
        launcher.Write("""{ "changed": true }""", "dalamudConfig.json");
        launcher.WritePluginConfig("BrandNewPlugin", "{}");
        var installedPlugin = Path.Combine(launcher.Root, "installedPlugins", "Artisan", "1.0.0", "Artisan.dll");
        File.WriteAllText(installedPlugin, "newer binary");

        var result = await host.Restores.RestoreAsync(new RestoreRequest(backup.Record.FilePath), cancellationToken: Ct);

        Assert.Equal(originalArtisan, launcher.ReadPluginConfig("Artisan"));
        Assert.Equal(originalConfig, launcher.Read("dalamudConfig.json"));
        Assert.Equal(FakeXivLauncher.DefaultPlugins.Length, result.PluginConfigCount);
        Assert.True(result.DalamudConfigRestored);
        Assert.Equal("newer binary", File.ReadAllText(installedPlugin));
        Assert.Equal("leave me alone", File.ReadAllText(Path.Combine(launcher.Root, "unrelated-file.txt")));
        Assert.Equal("{}", launcher.ReadPluginConfig("BrandNewPlugin"));
        Assert.Empty(Directory.GetFiles(launcher.DataPath, "*.xiv-vault-restore", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Every_restore_creates_a_pre_restore_snapshot_of_the_current_state()
    {
        using var host = new TestHost();
        var launcher = host.CreateLauncher();
        var backup = await host.BackUpAsync();
        launcher.WritePluginConfig("Artisan", """{ "today": true }""");

        var result = await host.Restores.RestoreAsync(new RestoreRequest(backup.Record.FilePath), cancellationToken: Ct);

        var snapshot = result.SafetySnapshot;
        Assert.StartsWith("pre-restore-2026-10-04-", snapshot.FileName);
        Assert.Equal(BackupKind.PreRestore, snapshot.Kind);
        Assert.Equal(IntegrityState.Verified, snapshot.Integrity);
        Assert.True(snapshot.Manifest!.Contents.DalamudUi, "The snapshot always keeps the UI layout a restore may replace.");

        // Restoring the snapshot brings back what was there before.
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        await host.Restores.RestoreAsync(new RestoreRequest(snapshot.FilePath), cancellationToken: Ct);
        Assert.Equal("""{ "today": true }""", launcher.ReadPluginConfig("Artisan"));
    }

    [Fact]
    public async Task Refuses_while_the_game_is_running_and_changes_nothing()
    {
        using var host = new TestHost();
        var launcher = host.CreateLauncher();
        var backup = await host.BackUpAsync();
        launcher.WritePluginConfig("Artisan", "current");
        host.Processes.Running.Add("ffxiv_dx11");

        var error = await Assert.ThrowsAsync<XivVaultException>(
            () => host.Restores.RestoreAsync(new RestoreRequest(backup.Record.FilePath), cancellationToken: Ct));

        Assert.Equal(XivVaultErrorKind.GameRunning, error.Kind);
        Assert.Equal("current", launcher.ReadPluginConfig("Artisan"));
        Assert.DoesNotContain(host.Catalog.List(host.BackupFolder), record => record.IsSafetySnapshot);
    }

    [Fact]
    public async Task Refuses_while_XIVLauncher_is_running()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var backup = await host.BackUpAsync();
        host.Processes.Running.Add("XIVLauncher");

        var error = await Assert.ThrowsAsync<XivVaultException>(
            () => host.Restores.RestoreAsync(new RestoreRequest(backup.Record.FilePath), cancellationToken: Ct));

        Assert.Equal(XivVaultErrorKind.GameRunning, error.Kind);
    }

    [Fact]
    public async Task Checksum_mismatch_blocks_the_restore()
    {
        using var host = new TestHost();
        var launcher = host.CreateLauncher();
        var backup = await host.BackUpAsync();
        RetentionTests.Corrupt(backup.Record.FilePath);
        launcher.WritePluginConfig("Artisan", "current");

        var error = await Assert.ThrowsAsync<XivVaultException>(
            () => host.Restores.RestoreAsync(new RestoreRequest(backup.Record.FilePath), cancellationToken: Ct));

        Assert.Equal(XivVaultErrorKind.RestoreValidationFailed, error.Kind);
        Assert.Equal("current", launcher.ReadPluginConfig("Artisan"));
        Assert.Equal(IntegrityState.Failed, host.Catalog.Read(backup.Record.FilePath)!.Integrity);
    }

    [Theory]
    [InlineData("payload/../../evil.txt")]
    [InlineData("payload/pluginConfigs/../../../evil.txt")]
    [InlineData("C:/evil.txt")]
    public async Task Hostile_archives_never_write_outside_the_target(string hostilePath)
    {
        using var host = new TestHost();
        var launcher = host.CreateLauncher();
        var archive = new ArchiveBuilder()
            .File("payload/dalamudConfig.json", "{}")
            .File(hostilePath, "evil")
            .Save(Path.Combine(host.BackupFolder, "xiv-vault-2026-09-28-183800.zip"));
        var before = OutsideXivVault(host.Environment.Root);

        var error = await Assert.ThrowsAsync<XivVaultException>(
            () => host.Restores.RestoreAsync(new RestoreRequest(archive), cancellationToken: Ct));

        Assert.Equal(XivVaultErrorKind.RestoreValidationFailed, error.Kind);
        Assert.False(File.Exists(Path.Combine(host.Environment.Root, "evil.txt")));
        Assert.False(File.Exists(@"C:\evil.txt"));
        Assert.NotEqual("{}", launcher.Read("dalamudConfig.json"));
        Assert.Equal(before, OutsideXivVault(host.Environment.Root));
    }

    [Fact]
    public async Task Unknown_files_in_the_archive_block_the_restore()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var archive = new ArchiveBuilder()
            .File("payload/dalamudConfig.json", "{}")
            .UnlistedEntry("payload/pluginConfigs/Hidden.json", "{}")
            .Save(Path.Combine(host.BackupFolder, "xiv-vault-2026-09-28-183800.zip"));

        var error = await Assert.ThrowsAsync<XivVaultException>(
            () => host.Restores.RestoreAsync(new RestoreRequest(archive), cancellationToken: Ct));

        Assert.Equal(XivVaultErrorKind.RestoreValidationFailed, error.Kind);
    }

    [Fact]
    public async Task Malformed_and_unsupported_manifests_block_the_restore()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var malformed = new ArchiveBuilder().File("payload/dalamudConfig.json", "{}").RawManifest("[]")
            .Save(Path.Combine(host.BackupFolder, "xiv-vault-2026-09-27-000000.zip"));
        var future = new ArchiveBuilder().File("payload/dalamudConfig.json", "{}").SchemaVersion(9)
            .Save(Path.Combine(host.BackupFolder, "xiv-vault-2026-09-28-000000.zip"));

        foreach (var archive in new[] { malformed, future })
        {
            var error = await Assert.ThrowsAsync<XivVaultException>(
                () => host.Restores.RestoreAsync(new RestoreRequest(archive), cancellationToken: Ct));
            Assert.Equal(XivVaultErrorKind.RestoreValidationFailed, error.Kind);
        }
    }

    [Fact]
    public async Task A_failure_part_way_through_puts_every_file_back()
    {
        // Plugin configs are applied first; the 7th write is dalamudConfig.json, after the
        // restore has already replaced five files and created a folder and a file.
        var writer = new FailingWriter(failOnCall: 7);
        using var host = new TestHost(configure: services => services.AddSingleton<IRestoreService>(provider => new RestoreService(
            provider.GetRequiredService<IConfigStore>(),
            provider.GetRequiredService<IXivLauncherLocator>(),
            provider.GetRequiredService<ArchiveValidator>(),
            provider.GetRequiredService<BackupCatalog>(),
            provider.GetRequiredService<BackupService>(),
            provider.GetRequiredService<PortableStateScanner>(),
            provider.GetRequiredService<GameProcessGuard>(),
            provider.GetRequiredService<PathDisplay>(),
            provider.GetRequiredService<IAppEnvironment>(),
            provider.GetRequiredService<RetentionService>(),
            provider.GetRequiredService<OperationLock>(),
            NullLogger<RestoreService>.Instance,
            writer)));
        var launcher = host.CreateLauncher();
        var backup = await host.BackUpAsync();

        foreach (var plugin in FakeXivLauncher.DefaultPlugins)
        {
            launcher.WritePluginConfig(plugin, "current " + plugin);
        }

        File.Delete(Path.Combine(launcher.PluginConfigs, "AutoRetainer", "profiles", "main.json"));
        Directory.Delete(Path.Combine(launcher.PluginConfigs, "AutoRetainer", "profiles"));
        var before = Snapshot(launcher.Root);

        var error = await Assert.ThrowsAsync<XivVaultException>(
            () => host.Restores.RestoreAsync(new RestoreRequest(backup.Record.FilePath), cancellationToken: Ct));

        Assert.Equal(XivVaultErrorKind.Unexpected, error.Kind);
        Assert.Contains("put back", error.Message);
        Assert.Equal(7, writer.Calls);
        Assert.False(Directory.Exists(Path.Combine(launcher.PluginConfigs, "AutoRetainer", "profiles")));
        foreach (var plugin in FakeXivLauncher.DefaultPlugins)
        {
            Assert.Equal("current " + plugin, launcher.ReadPluginConfig(plugin));
        }

        Assert.Equal(before, Snapshot(launcher.Root));
        Assert.Contains(host.Catalog.List(host.BackupFolder), record => record.IsSafetySnapshot);
    }

    [Fact]
    public async Task Restores_onto_a_fresh_PC_that_only_ran_XIVLauncher()
    {
        using var host = new TestHost();
        var oldPc = FakeXivLauncher.Create(Path.Combine(host.Environment.Root, "OldPC", "XIVLauncher"));
        var backup = await host.Backups.CreateBackupAsync(new BackupRequest(BackupKind.Manual) { Source = oldPc.Root }, cancellationToken: Ct);
        Directory.CreateDirectory(host.Environment.XivLauncherPath);
        File.WriteAllText(Path.Combine(host.Environment.XivLauncherPath, "launcherConfigV3.json"), "{}");

        var result = await host.Restores.RestoreAsync(new RestoreRequest(backup.Record.FilePath), cancellationToken: Ct);

        Assert.Equal(oldPc.ReadPluginConfig("Splatoon"), File.ReadAllText(Path.Combine(host.Environment.XivLauncherPath, "pluginConfigs", "Splatoon.json")));
        Assert.True(File.Exists(Path.Combine(host.Environment.XivLauncherPath, "dalamudConfig.json")));
        Assert.Empty(result.SafetySnapshot.Manifest!.Files);
    }

    [Fact]
    public async Task Restore_follows_the_layout_of_the_target()
    {
        using var host = new TestHost();
        var oldPc = FakeXivLauncher.Create(Path.Combine(host.Environment.Root, "OldPC", "XIVLauncher"));
        var backup = await host.Backups.CreateBackupAsync(new BackupRequest(BackupKind.Manual) { Source = oldPc.Root }, cancellationToken: Ct);
        var target = host.CreateLauncher(userDataLayout: true);
        target.WritePluginConfig("Splatoon", "different");

        await host.Restores.RestoreAsync(new RestoreRequest(backup.Record.FilePath), cancellationToken: Ct);

        Assert.Equal(oldPc.ReadPluginConfig("Splatoon"), target.ReadPluginConfig("Splatoon"));
        Assert.False(File.Exists(Path.Combine(target.Root, "pluginConfigs", "Splatoon.json")));
    }

    [Fact]
    public async Task Missing_backup_folder_blocks_the_restore_because_no_snapshot_can_be_made()
    {
        using var host = new TestHost();
        var launcher = host.CreateLauncher();
        var backup = await host.BackUpAsync();
        var freeLetter = "QRSTUVWXYZ".First(letter => !Directory.Exists($"{letter}:\\"));
        host.UpdateConfig(config => config with { BackupDestination = $@"{freeLetter}:\XIV Vault" });
        launcher.WritePluginConfig("Artisan", "current");

        var error = await Assert.ThrowsAsync<XivVaultException>(
            () => host.Restores.RestoreAsync(new RestoreRequest(backup.Record.FilePath), cancellationToken: Ct));

        Assert.Equal(XivVaultErrorKind.DestinationUnavailable, error.Kind);
        Assert.Equal("current", launcher.ReadPluginConfig("Artisan"));
    }

    [Fact]
    public async Task Safety_checks_report_each_blocker()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var backup = await host.BackUpAsync();

        var passing = await host.Restores.CheckAsync(backup.Record.FilePath, cancellationToken: Ct);
        Assert.All(passing, check => Assert.True(check.Passed, check.Label));
        Assert.Equal(5, passing.Count);

        host.Processes.Running.Add("ffxiv_dx11");
        RetentionTests.Corrupt(backup.Record.FilePath);
        var failing = await host.Restores.CheckAsync(backup.Record.FilePath, cancellationToken: Ct);
        Assert.False(failing.Single(check => check.Id == SafetyCheckId.GameClosed).Passed);
        Assert.False(failing.Single(check => check.Id == SafetyCheckId.IntegrityVerified).Passed);
        Assert.True(failing.Single(check => check.Id == SafetyCheckId.XivLauncherClosed).Passed);
    }

    [Fact]
    public async Task Preview_compares_the_backup_with_this_PC()
    {
        using var host = new TestHost();
        var launcher = host.CreateLauncher();
        var backup = await host.BackUpAsync();
        foreach (var file in Directory.EnumerateFiles(launcher.PluginConfigs, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, backup.Record.CreatedAtUtc.AddHours(-2));
        }

        File.SetLastWriteTimeUtc(Path.Combine(launcher.PluginConfigs, "Splatoon.json"), host.Clock.Now.UtcDateTime.AddHours(1));
        launcher.WritePluginConfig("NotInBackup", "{}");
        File.SetLastWriteTimeUtc(Path.Combine(launcher.PluginConfigs, "NotInBackup.json"), host.Clock.Now.UtcDateTime.AddHours(1));

        var preview = await host.Restores.PreviewAsync(backup.Record.FilePath, cancellationToken: Ct);

        Assert.Equal(FakeXivLauncher.DefaultPlugins.Length, preview.Contents.PluginConfigCount);
        Assert.Equal(2, preview.Contents.CustomRepositoryCount);
        Assert.False(preview.Contents.DalamudUi);
        Assert.Equal(FakeXivLauncher.DefaultPlugins.Length + 1, preview.Current!.PluginConfigCount);
        Assert.Equal(["Splatoon"], preview.PluginsChangedSinceBackup);
        Assert.True(preview.IsOlderThanCurrent);
    }

    /// <summary>Every file except XIV Vault's own folders (backups, state), which a restore attempt may update.</summary>
    private static List<string> OutsideXivVault(string root) =>
        Snapshot(root).Where(path => !path.Contains(@"\XIV Vault\", StringComparison.Ordinal)).ToList();

    private static List<string> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => path + "|" + File.ReadAllText(path).GetHashCode(StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

    private sealed class FailingWriter(int failOnCall) : IRestoreFileWriter
    {
        private readonly RenameRestoreFileWriter _inner = new();

        public int Calls { get; private set; }

        public void Replace(string stagedPath, string targetPath)
        {
            Calls++;
            if (Calls == failOnCall)
            {
                throw new IOException("Simulated disk failure");
            }

            _inner.Replace(stagedPath, targetPath);
        }
    }
}

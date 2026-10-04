using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using XivVault.Core;
using XivVault.Core.Backup;
using XivVault.Core.Configuration;
using XivVault.Core.Diagnostics;
using XivVault.Core.Discovery;
using XivVault.Core.Platform;
using XivVault.Core.Restore;
using XivVault.Tests.Support;

namespace XivVault.Tests.Core;

/// <summary>Failure modes found in review: each test pins one down.</summary>
public class RestoreHardeningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Restoring_the_oldest_safety_snapshot_keeps_it()
    {
        using var host = new TestHost();
        var launcher = host.CreateLauncher();
        var backup = await host.BackUpAsync();
        var snapshots = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            launcher.WritePluginConfig("Artisan", $"state {i}");
            snapshots.Add((await host.Restores.RestoreAsync(new RestoreRequest(backup.Record.FilePath), cancellationToken: Ct)).SafetySnapshot.FilePath);
            host.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        var oldest = snapshots[0];
        var result = await host.Restores.RestoreAsync(new RestoreRequest(oldest), cancellationToken: Ct);

        Assert.True(File.Exists(oldest), "The archive being restored must survive snapshot retention.");
        Assert.Equal("state 0", launcher.ReadPluginConfig("Artisan"));
        Assert.True(File.Exists(result.SafetySnapshot.FilePath));
    }

    [Fact]
    public async Task Restore_refuses_to_write_through_a_junction_and_changes_nothing()
    {
        using var host = new TestHost();
        var launcher = host.CreateLauncher();
        var backup = await host.BackUpAsync();

        // Replace pluginConfigs\AutoRetainer with a junction to a folder outside XIVLauncher.
        var elsewhere = Path.Combine(host.Environment.Root, "Elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "keep.json"), "outside");
        var linked = Path.Combine(launcher.PluginConfigs, "AutoRetainer");
        Directory.Delete(linked, recursive: true);
        CreateJunction(linked, elsewhere);
        launcher.WritePluginConfig("Artisan", "current");

        var error = await Assert.ThrowsAsync<XivVaultException>(
            () => host.Restores.RestoreAsync(new RestoreRequest(backup.Record.FilePath), cancellationToken: Ct));

        Assert.Equal(XivVaultErrorKind.RestoreValidationFailed, error.Kind);
        Assert.Contains("link", error.Message);
        Assert.Equal("current", launcher.ReadPluginConfig("Artisan"));
        Assert.Equal(["keep.json"], Directory.GetFiles(elsewhere).Select(Path.GetFileName));
        Assert.DoesNotContain(host.Catalog.List(host.BackupFolder), record => record.IsSafetySnapshot);
    }

    [Fact]
    public async Task Diagnostics_warn_when_pluginConfigs_is_a_link()
    {
        using var host = new TestHost();
        var launcher = host.CreateLauncher();
        var real = Path.Combine(host.Environment.Root, "RealConfigs");
        Directory.Move(launcher.PluginConfigs, real);
        CreateJunction(launcher.PluginConfigs, real);

        var report = await host.Get<IDiagnosticsService>().RunAsync(Ct);

        Assert.Contains(report.Groups[1].Checks, check => check.Label == "Plugin configuration directory is a link" && check.Status == DiagnosticStatus.Warning);
    }

    [Fact]
    public async Task Read_only_files_are_restored_stay_read_only_and_leave_no_copies_behind()
    {
        using var host = new TestHost();
        var launcher = host.CreateLauncher();
        var backup = await host.BackUpAsync();
        var original = launcher.ReadPluginConfig("Artisan");
        var artisan = Path.Combine(launcher.PluginConfigs, "Artisan.json");
        File.WriteAllText(artisan, "current");
        File.SetAttributes(artisan, FileAttributes.ReadOnly);

        await host.Restores.RestoreAsync(new RestoreRequest(backup.Record.FilePath), cancellationToken: Ct);

        Assert.Equal(original, File.ReadAllText(artisan));
        Assert.True(File.GetAttributes(artisan).HasFlag(FileAttributes.ReadOnly));
        var work = Path.Combine(host.Environment.TempPath, "XIV Vault");
        Assert.True(!Directory.Exists(work) || Directory.GetDirectories(work, "restore-*").Length == 0);
        File.SetAttributes(artisan, FileAttributes.Normal);
    }

    [Fact]
    public async Task A_failure_after_the_temp_copy_is_written_still_rolls_everything_back()
    {
        var writer = new FailAfterTempCopy(failOnCall: 4);
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

        var error = await Assert.ThrowsAsync<XivVaultException>(
            () => host.Restores.RestoreAsync(new RestoreRequest(backup.Record.FilePath), cancellationToken: Ct));

        Assert.Contains("put back", error.Message);
        foreach (var plugin in FakeXivLauncher.DefaultPlugins)
        {
            Assert.Equal("current " + plugin, launcher.ReadPluginConfig(plugin));
        }

        Assert.Empty(Directory.GetFiles(launcher.DataPath, "*.xiv-vault-restore", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_manifest_that_misdescribes_its_files_is_refused()
    {
        using var host = new TestHost();
        var launcher = host.CreateLauncher();
        var archive = new ArchiveBuilder()
            .File("payload/dalamudConfig.json", """{ "ThirdRepoList": [ { "Url": "https://evil.example.com" } ] }""")
            .ClaimContents(pluginConfigs: false, dalamudConfig: false, dalamudVfs: false, dalamudUi: false)
            .Save(Path.Combine(host.BackupFolder, "xiv-vault-2026-09-28-183800.zip"));

        var error = await Assert.ThrowsAsync<XivVaultException>(
            () => host.Restores.RestoreAsync(new RestoreRequest(archive), cancellationToken: Ct));

        Assert.Equal(XivVaultErrorKind.RestoreValidationFailed, error.Kind);
        Assert.DoesNotContain("evil", launcher.Read("dalamudConfig.json"));
    }

    [Fact]
    public async Task Archives_cannot_write_into_folders_backups_skip()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var archive = new ArchiveBuilder()
            .File("payload/dalamudConfig.json", "{}")
            .File("payload/pluginConfigs/AutoRetainer/cache/icons.bin", "replaced")
            .Save(Path.Combine(host.BackupFolder, "xiv-vault-2026-09-28-183800.zip"));

        var error = await Assert.ThrowsAsync<XivVaultException>(
            () => host.Restores.RestoreAsync(new RestoreRequest(archive), cancellationToken: Ct));

        Assert.Equal(XivVaultErrorKind.RestoreValidationFailed, error.Kind);
    }

    [Fact]
    public async Task Null_file_entries_are_a_malformed_manifest_not_a_crash()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var archive = new ArchiveBuilder()
            .File("payload/dalamudConfig.json", "{}")
            .RawManifest("""
                {
                  "schemaVersion": 1, "xivVaultVersion": "0.1.0", "createdAtUtc": "2026-09-28T18:38:00Z", "backupType": "manual",
                  "source": {}, "contents": {}, "statistics": {}, "files": [ null ]
                }
                """)
            .Save(Path.Combine(host.BackupFolder, "xiv-vault-2026-09-28-183800.zip"));

        var listed = host.Catalog.List(host.BackupFolder);
        var error = await Assert.ThrowsAsync<XivVaultException>(() => host.Restores.PreviewAsync(archive, cancellationToken: Ct));

        Assert.Equal(IntegrityState.Failed, Assert.Single(listed).Integrity);
        Assert.Equal(XivVaultErrorKind.RestoreValidationFailed, error.Kind);
    }

    [Fact]
    public async Task The_operation_lock_admits_one_holder_at_a_time()
    {
        using var env = new TestEnvironment();
        var gate = new OperationLock(env);
        var holding = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        // A mutex belongs to the thread that took it, so each holder takes and releases it on its own thread.
        var holder = Task.Factory.StartNew(
            () =>
            {
                using var held = gate.Acquire();
                holding.SetResult();
                release.Task.Wait();
            },
            Ct,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        await holding.Task;

        var error = await Assert.ThrowsAsync<XivVaultException>(() => Task.Run(() => gate.Acquire(TimeSpan.FromMilliseconds(50)).Dispose(), Ct));
        Assert.Contains("still running", error.Message);

        release.SetResult();
        await holder;
        await Task.Run(() => gate.Acquire(TimeSpan.FromSeconds(5)).Dispose(), Ct);
    }

    internal static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    /// <summary>Writes the temp copy beside the target, then fails before the rename.</summary>
    private sealed class FailAfterTempCopy(int failOnCall) : IRestoreFileWriter
    {
        private readonly RenameRestoreFileWriter _inner = new();
        private int _calls;

        public void Replace(string stagedPath, string targetPath)
        {
            if (++_calls == failOnCall)
            {
                File.Copy(stagedPath, targetPath + ".xiv-vault-restore", overwrite: true);
                throw new IOException("Simulated failure during the rename");
            }

            _inner.Replace(stagedPath, targetPath);
        }
    }
}

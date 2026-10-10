using System.IO.Compression;
using System.Security.Cryptography;
using XivVault.Core;
using XivVault.Core.Backup;
using XivVault.Tests.Support;

namespace XivVault.Tests.Core;

public class BackupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Creates_a_verified_archive_with_a_timestamped_name()
    {
        using var host = new TestHost();
        host.CreateLauncher();

        var result = await host.BackUpAsync();

        Assert.Equal("xiv-vault-2026-10-04-131900.zip", result.Record.FileName);
        Assert.Equal(host.BackupFolder, Path.GetDirectoryName(result.Record.FilePath));
        Assert.True(File.Exists(result.Record.FilePath));
        Assert.Equal(IntegrityState.Verified, result.Record.Integrity);
        Assert.Empty(Directory.GetFiles(host.BackupFolder, "*.tmp"));
    }

    [Fact]
    public async Task Writes_a_versioned_manifest()
    {
        using var host = new TestHost();
        host.CreateLauncher();

        var manifest = (await host.BackUpAsync(BackupKind.Scheduled)).Manifest;

        Assert.Equal(2, manifest.SchemaVersion);
        Assert.Equal(XivVaultInfo.Version, manifest.XivVaultVersion);
        Assert.Equal(new DateTime(2026, 10, 4, 13, 19, 0, DateTimeKind.Utc), manifest.CreatedAtUtc);
        Assert.Equal(BackupKind.Scheduled, manifest.BackupType);
        Assert.Equal("windows", manifest.Source.Platform);
        Assert.True(manifest.Contents.PluginConfigs);
        Assert.True(manifest.Contents.DalamudConfig);
        Assert.True(manifest.Contents.DalamudVfs);
        Assert.False(manifest.Contents.DalamudUi);
        Assert.Equal(FakeXivLauncher.DefaultPlugins.Length, manifest.Statistics.PluginConfigCount);
        Assert.Equal(manifest.Files.Count, manifest.Statistics.FileCount);

        using var zip = ZipFile.OpenRead((await host.BackUpAsync()).Record.FilePath);
        var json = new StreamReader(zip.GetEntry("manifest.json")!.Open()).ReadToEnd();
        Assert.Contains("\"schemaVersion\": 2", json);
        Assert.Contains("\"createdAtUtc\": \"2026-10-04T13:20:00Z\"", json);
        Assert.Contains("\"backupType\": \"manual\"", json);
    }

    [Fact]
    public async Task Archives_only_allowlisted_files()
    {
        using var host = new TestHost();
        host.CreateLauncher();

        var record = (await host.BackUpAsync()).Record;
        using var zip = ZipFile.OpenRead(record.FilePath);
        var names = zip.Entries.Select(entry => entry.FullName).ToList();

        Assert.Contains("manifest.json", names);
        Assert.Contains("payload/dalamudConfig.json", names);
        Assert.Contains("payload/dalamudVfs.db", names);
        Assert.Contains("payload/pluginConfigs/Artisan.json", names);
        Assert.All(names.Where(name => name != "manifest.json"), name => Assert.StartsWith("payload/", name));
        Assert.DoesNotContain(names, name => name.Contains("installedPlugins", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("runtime", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("addon", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("launcherConfig", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("unrelated", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.EndsWith(".log", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Recurses_into_plugin_folders_but_skips_caches_and_temp_files()
    {
        using var host = new TestHost();
        host.CreateLauncher();

        var manifest = (await host.BackUpAsync()).Manifest;
        var paths = manifest.Files.Select(file => file.Path).ToList();

        Assert.Contains("payload/pluginConfigs/AutoRetainer/profiles/main.json", paths);
        Assert.DoesNotContain(paths, path => path.Contains("/cache/", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UI_layout_is_left_out_unless_enabled()
    {
        using var host = new TestHost();
        host.CreateLauncher();

        var without = (await host.BackUpAsync()).Manifest;
        host.UpdateConfig(config => config with { IncludeDalamudUi = true });
        var with = (await host.BackUpAsync()).Manifest;

        Assert.DoesNotContain(without.Files, file => file.Path == "payload/dalamudUI.ini");
        Assert.Contains(with.Files, file => file.Path == "payload/dalamudUI.ini");
        Assert.True(with.Contents.DalamudUi);
    }

    [Fact]
    public async Task Records_the_SHA256_of_every_file()
    {
        using var host = new TestHost();
        var launcher = host.CreateLauncher();

        var manifest = (await host.BackUpAsync()).Manifest;
        var config = manifest.Files.Single(file => file.Path == "payload/dalamudConfig.json");

        var expected = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(launcher.DataPath, "dalamudConfig.json"))));
        Assert.Equal(expected, config.Sha256);
        Assert.Equal(new FileInfo(Path.Combine(launcher.DataPath, "dalamudConfig.json")).Length, config.Size);
    }

    [Fact]
    public async Task Backs_up_the_dalamudUserData_layout()
    {
        using var host = new TestHost();
        host.CreateLauncher(userDataLayout: true);

        var manifest = (await host.BackUpAsync()).Manifest;

        Assert.Equal("dalamudUserData", manifest.Source.Layout);
        Assert.Contains(manifest.Files, file => file.Path == "payload/dalamudConfig.json");
    }

    [Fact]
    public async Task A_cancelled_backup_leaves_nothing_behind_and_keeps_old_backups()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var existing = (await host.BackUpAsync()).Record.FilePath;

        using var cancel = new CancellationTokenSource();
        var progress = new SynchronousProgress<BackupProgress>(report =>
        {
            if (report.Stage == BackupStage.CompressingPluginConfigs)
            {
                cancel.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.Backups.CreateBackupAsync(new BackupRequest(BackupKind.Manual), progress, cancel.Token));

        Assert.Equal([existing], Directory.GetFiles(host.BackupFolder));
    }

    [Fact]
    public async Task Missing_installation_fails_with_not_found()
    {
        using var host = new TestHost();

        var error = await Assert.ThrowsAsync<XivVaultException>(() => host.BackUpAsync());

        Assert.Equal(XivVaultErrorKind.XivLauncherNotFound, error.Kind);
    }

    [Fact]
    public async Task Unavailable_destination_fails_without_touching_anything()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var blocker = Path.Combine(host.Environment.Root, "not-a-folder");
        File.WriteAllText(blocker, "a file where the folder should be");

        var error = await Assert.ThrowsAsync<XivVaultException>(
            () => host.Backups.CreateBackupAsync(new BackupRequest(BackupKind.Manual) { Destination = Path.Combine(blocker, "XIV Vault") }, cancellationToken: Ct));

        Assert.Equal(XivVaultErrorKind.DestinationUnavailable, error.Kind);
    }

    [Fact]
    public async Task Missing_drive_is_destination_unavailable()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var freeLetter = "QRSTUVWXYZ".First(letter => !Directory.Exists($"{letter}:\\"));

        var error = await Assert.ThrowsAsync<XivVaultException>(
            () => host.Backups.CreateBackupAsync(new BackupRequest(BackupKind.Manual) { Destination = $@"{freeLetter}:\XIV Vault" }, cancellationToken: Ct));

        Assert.Equal(XivVaultErrorKind.DestinationUnavailable, error.Kind);
    }

    [Fact]
    public async Task Two_backups_in_the_same_second_get_distinct_names()
    {
        using var host = new TestHost();
        host.CreateLauncher();

        var first = await host.Backups.CreateBackupAsync(new BackupRequest(BackupKind.Manual), cancellationToken: Ct);
        var second = await host.Backups.CreateBackupAsync(new BackupRequest(BackupKind.Manual), cancellationToken: Ct);

        Assert.Equal("xiv-vault-2026-10-04-131900.zip", first.Record.FileName);
        Assert.Equal("xiv-vault-2026-10-04-131900-2.zip", second.Record.FileName);
    }

    [Fact]
    public async Task Archive_opens_in_ordinary_zip_tools()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var record = (await host.BackUpAsync()).Record;
        var extracted = Path.Combine(host.Environment.Root, "extracted");

        ZipFile.ExtractToDirectory(record.FilePath, extracted);

        Assert.True(File.Exists(Path.Combine(extracted, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(extracted, "payload", "pluginConfigs", "Splatoon.json")));
    }
}

/// <summary>Progress&lt;T&gt; posts to the thread pool; tests need the callback to run inline.</summary>
public sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}

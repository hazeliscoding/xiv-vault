using XivVault.Core;
using XivVault.Core.Backup;
using XivVault.Core.Diagnostics;
using XivVault.Core.Platform;
using XivVault.Core.Status;
using XivVault.Tests.Support;

namespace XivVault.Tests.Core;

/// <summary>Backup folders synced by OneDrive and similar apps, where a file may be online only.</summary>
public class OnlineOnlyTests
{
    [Fact]
    public void A_file_whose_contents_are_not_on_this_pc_is_online_only()
    {
        using var environment = new TestEnvironment();
        var path = Path.Combine(environment.Root, "backup.zip");
        File.WriteAllText(path, "zip");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Offline);

        Assert.True(new WindowsFileAvailability().IsOnlineOnly(path));
    }

    [Fact]
    public void A_normal_or_missing_file_is_not_online_only()
    {
        using var environment = new TestEnvironment();
        var path = Path.Combine(environment.Root, "backup.zip");
        File.WriteAllText(path, "zip");

        var availability = new WindowsFileAvailability();

        Assert.False(availability.IsOnlineOnly(path));
        Assert.False(availability.IsOnlineOnly(Path.Combine(environment.Root, "missing.zip")));
    }

    [Fact]
    public async Task Listing_describes_an_online_only_backup_by_its_name_without_opening_it()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var backup = await host.BackUpAsync();
        var size = new FileInfo(backup.Record.FilePath).Length;
        host.MoveToCloud(backup.Record.FilePath);

        var record = Assert.Single(host.Catalog.List(host.BackupFolder));

        Assert.True(record.IsOnlineOnly);
        Assert.True(record.IsRecognized);
        Assert.Null(record.Manifest);
        Assert.Null(record.Problem);
        Assert.Equal(IntegrityState.Unverified, record.Integrity);
        Assert.Equal(size, record.SizeBytes);
        Assert.Equal(new DateTime(2026, 10, 4, 13, 19, 0, DateTimeKind.Utc), record.CreatedAtUtc);
        Assert.False(record.IsSafetySnapshot);
    }

    [Fact]
    public void An_online_only_safety_snapshot_is_known_by_its_name()
    {
        using var host = new TestHost();
        host.AddCloudFile("pre-restore-2026-10-01-080000-2.zip");

        var record = Assert.Single(host.Catalog.List(host.BackupFolder));

        Assert.True(record.IsSafetySnapshot);
        Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc), record.CreatedAtUtc);
    }

    [Fact]
    public void An_online_only_backup_without_a_date_in_its_name_is_dated_by_the_file()
    {
        using var host = new TestHost();
        var path = host.AddCloudFile("xiv-vault-before-reinstall.zip");
        var modified = new DateTime(2026, 9, 20, 18, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, modified);

        var record = Assert.Single(host.Catalog.List(host.BackupFolder));

        Assert.Equal(modified, record.CreatedAtUtc);
        Assert.Null(record.Problem);
    }

    [Fact]
    public void A_time_in_the_name_that_this_pc_skips_for_daylight_saving_is_dated_by_the_file()
    {
        // Another PC's zone can name a backup with an hour that doesn't exist here.
        using var host = new TestHost();
        host.Clock.Zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var path = host.AddCloudFile("xiv-vault-2026-03-08-023000.zip");
        var modified = new DateTime(2026, 3, 8, 7, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, modified);

        var record = Assert.Single(host.Catalog.List(host.BackupFolder));

        Assert.Equal(modified, record.CreatedAtUtc);
    }

    [Fact]
    public void Online_only_zips_with_names_xiv_vault_does_not_use_are_not_listed()
    {
        using var host = new TestHost();
        host.AddCloudFile("photos.zip");

        Assert.Empty(host.Catalog.List(host.BackupFolder));
    }

    [Fact]
    public async Task A_verification_this_pc_remembers_still_applies_while_the_backup_is_online_only()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var backup = await host.BackUpAsync();
        host.Files.OnlineOnly.Add(backup.Record.FilePath);

        var record = Assert.Single(host.Catalog.List(host.BackupFolder));

        Assert.True(record.IsOnlineOnly);
        Assert.Equal(IntegrityState.Verified, record.Integrity);
    }

    [Fact]
    public void Reading_a_chosen_online_only_file_does_not_open_it_either()
    {
        using var host = new TestHost();
        var path = host.AddCloudFile("renamed backup.zip");

        var record = host.Catalog.Read(path);

        Assert.NotNull(record);
        Assert.True(record.IsOnlineOnly);
        Assert.Null(record.Problem);
    }

    [Fact]
    public async Task Downloading_an_online_only_backup_reads_its_manifest_and_reports_progress()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var backup = await host.BackUpAsync();
        host.MoveToCloud(backup.Record.FilePath);
        var listed = Assert.Single(host.Catalog.List(host.BackupFolder));
        var progress = new Recorded<long>();

        var downloaded = host.Catalog.Download(listed, progress, Ct);

        Assert.False(downloaded.IsOnlineOnly);
        Assert.True(downloaded.HasManifest);
        Assert.Equal(listed.SizeBytes, progress.Values[^1]);
    }

    [Fact]
    public void Downloading_a_file_that_is_not_a_backup_says_so()
    {
        using var host = new TestHost();
        var record = host.Catalog.Read(host.AddCloudFile("renamed backup.zip"))!;

        var error = Assert.Throws<XivVaultException>(() => host.Catalog.Download(record, null, Ct));

        Assert.Equal(XivVaultErrorKind.RestoreValidationFailed, error.Kind);
        Assert.Contains("is not a XIV Vault backup", error.Message);
    }

    [Fact]
    public void Downloading_a_damaged_backup_says_what_is_wrong()
    {
        using var host = new TestHost();
        host.AddCloudFile("xiv-vault-2026-10-01-080000.zip");
        var listed = Assert.Single(host.Catalog.List(host.BackupFolder));

        var error = Assert.Throws<XivVaultException>(() => host.Catalog.Download(listed, null, Ct));

        Assert.Equal(XivVaultErrorKind.RestoreValidationFailed, error.Kind);
        Assert.Contains("could not be read", error.Message);
    }

    [Fact]
    public async Task A_download_that_fails_says_what_to_check()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var backup = await host.BackUpAsync();
        host.MoveToCloud(backup.Record.FilePath);
        var listed = Assert.Single(host.Catalog.List(host.BackupFolder));
        using var unreachable = new FileStream(backup.Record.FilePath, FileMode.Open, FileAccess.Read, FileShare.None);

        var error = Assert.Throws<XivVaultException>(() => host.Catalog.Download(listed, null, Ct));

        Assert.Equal(XivVaultErrorKind.DestinationUnavailable, error.Kind);
        Assert.Contains("online", error.Message);
    }

    [Fact]
    public async Task Verifying_an_online_only_backup_downloads_and_reads_it()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var backup = await host.BackUpAsync();
        host.MoveToCloud(backup.Record.FilePath);
        var listed = Assert.Single(host.Catalog.List(host.BackupFolder));

        var verified = host.Catalog.Verify(listed, Ct);

        Assert.Equal(IntegrityState.Verified, verified.Integrity);
        Assert.False(verified.IsOnlineOnly);
        Assert.True(verified.HasManifest);
    }

    [Fact]
    public async Task Verifying_a_backup_that_cannot_be_downloaded_does_not_mark_it_damaged()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var backup = await host.BackUpAsync();
        host.MoveToCloud(backup.Record.FilePath);
        var listed = Assert.Single(host.Catalog.List(host.BackupFolder));

        using (new FileStream(backup.Record.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Assert.Throws<XivVaultException>(() => host.Catalog.Verify(listed, Ct));
            Assert.Equal(XivVaultErrorKind.DestinationUnavailable, error.Kind);
        }

        Assert.Equal(IntegrityState.Unverified, Assert.Single(host.Catalog.List(host.BackupFolder)).Integrity);
    }

    [Fact]
    public async Task Previewing_an_online_only_backup_downloads_it_first()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var backup = await host.BackUpAsync();
        host.MoveToCloud(backup.Record.FilePath);

        var preview = await host.Restores.PreviewAsync(backup.Record.FilePath, cancellationToken: Ct);

        Assert.True(preview.Backup.HasManifest);
        Assert.Equal(5, preview.Contents.PluginConfigCount);
    }

    [Fact]
    public async Task Retention_skips_online_only_backups_among_the_newest_instead_of_downloading_them()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.UpdateConfig(config => config with { RetentionCount = 2 });
        string[] cloud =
        [
            host.AddCloudFile("xiv-vault-2026-10-01-080000.zip"),
            host.AddCloudFile("xiv-vault-2026-10-02-080000.zip"),
            host.AddCloudFile("xiv-vault-2026-10-03-080000.zip"),
        ];

        var result = await host.BackUpAsync();

        Assert.Empty(result.RemovedByRetention);
        Assert.All(cloud, path => Assert.True(File.Exists(path)));
        Assert.All(
            host.Catalog.List(host.BackupFolder).Where(record => record.IsOnlineOnly),
            record => Assert.Equal(IntegrityState.Unverified, record.Integrity));
    }

    [Fact]
    public async Task Retention_deletes_online_only_backups_past_the_limit_without_opening_them()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.UpdateConfig(config => config with { RetentionCount = 2 });
        var older = host.AddCloudFile("xiv-vault-2026-10-01-080000.zip");
        var oldest = host.AddCloudFile("xiv-vault-2026-09-30-080000.zip");

        var first = await host.BackUpAsync();
        var second = await host.BackUpAsync();

        Assert.Equal([older, oldest], second.RemovedByRetention);
        Assert.True(File.Exists(first.Record.FilePath));
        Assert.True(File.Exists(second.Record.FilePath));
    }

    [Fact]
    public async Task Retention_counts_an_online_only_backup_this_pc_has_verified()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.UpdateConfig(config => config with { RetentionCount = 2 });
        var oldest = await host.BackUpAsync();
        var freedUp = await host.BackUpAsync();
        host.Files.OnlineOnly.Add(freedUp.Record.FilePath);

        var newest = await host.BackUpAsync();

        Assert.Equal([oldest.Record.FilePath], newest.RemovedByRetention);
        Assert.True(File.Exists(freedUp.Record.FilePath));
    }

    [Fact]
    public async Task Retention_keeps_an_online_only_backup_known_to_be_damaged()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.UpdateConfig(config => config with { RetentionCount = 1 });
        var damaged = await host.BackUpAsync();
        RetentionTests.Corrupt(damaged.Record.FilePath);
        host.Catalog.Verify(host.Catalog.Read(damaged.Record.FilePath)!, Ct);
        host.Files.OnlineOnly.Add(damaged.Record.FilePath);
        var middle = await host.BackUpAsync();

        var newest = await host.BackUpAsync();

        Assert.Equal([middle.Record.FilePath], newest.RemovedByRetention);
        Assert.True(File.Exists(damaged.Record.FilePath));
    }

    [Fact]
    public async Task Status_counts_an_online_only_backup_as_the_latest_without_verifying_it()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var path = host.AddCloudFile("xiv-vault-2026-10-04-080000.zip");

        var status = await host.Get<IStatusService>().GetAsync(verifyLatest: true, Ct);

        Assert.Equal(path, status.LatestBackup?.FilePath);
        Assert.Equal(IntegrityState.Unverified, status.LatestBackup!.Integrity);
        Assert.Equal(ProtectionState.Protected, status.State);
    }

    [Fact]
    public async Task Diagnostics_report_an_online_only_latest_backup_as_in_the_cloud()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.AddCloudFile("xiv-vault-2026-10-04-080000.zip");

        var report = await host.Get<IDiagnosticsService>().RunAsync(Ct);

        Assert.Contains(report.Groups.Single(group => group.Area == DiagnosticArea.BackupDestination).Checks, check => check.Label == "Latest backup is in the cloud" && check.Status == DiagnosticStatus.Healthy);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Recorded<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }
}

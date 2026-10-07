using XivVault.Core;
using XivVault.Core.Backup;
using XivVault.Core.Platform;
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
        MoveToCloud(host, backup.Record.FilePath);

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
        AddCloudFile(host, "pre-restore-2026-10-01-080000-2.zip");

        var record = Assert.Single(host.Catalog.List(host.BackupFolder));

        Assert.True(record.IsSafetySnapshot);
        Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc), record.CreatedAtUtc);
    }

    [Fact]
    public void An_online_only_backup_without_a_date_in_its_name_is_dated_by_the_file()
    {
        using var host = new TestHost();
        var path = AddCloudFile(host, "xiv-vault-before-reinstall.zip");
        var modified = new DateTime(2026, 9, 20, 18, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, modified);

        var record = Assert.Single(host.Catalog.List(host.BackupFolder));

        Assert.Equal(modified, record.CreatedAtUtc);
        Assert.Null(record.Problem);
    }

    [Fact]
    public void Online_only_zips_with_names_xiv_vault_does_not_use_are_not_listed()
    {
        using var host = new TestHost();
        AddCloudFile(host, "photos.zip");

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
        var path = AddCloudFile(host, "renamed backup.zip");

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
        MoveToCloud(host, backup.Record.FilePath);
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
        var record = host.Catalog.Read(AddCloudFile(host, "renamed backup.zip"))!;

        var error = Assert.Throws<XivVaultException>(() => host.Catalog.Download(record, null, Ct));

        Assert.Equal(XivVaultErrorKind.RestoreValidationFailed, error.Kind);
        Assert.Contains("is not a XIV Vault backup", error.Message);
    }

    [Fact]
    public void Downloading_a_damaged_backup_says_what_is_wrong()
    {
        using var host = new TestHost();
        AddCloudFile(host, "xiv-vault-2026-10-01-080000.zip");
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
        MoveToCloud(host, backup.Record.FilePath);
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
        MoveToCloud(host, backup.Record.FilePath);
        var listed = Assert.Single(host.Catalog.List(host.BackupFolder));

        var verified = host.Catalog.Verify(listed, Ct);

        Assert.Equal(IntegrityState.Verified, verified.Integrity);
        Assert.False(verified.IsOnlineOnly);
        Assert.True(verified.HasManifest);
    }

    [Fact]
    public async Task Previewing_an_online_only_backup_downloads_it_first()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var backup = await host.BackUpAsync();
        MoveToCloud(host, backup.Record.FilePath);

        var preview = await host.Restores.PreviewAsync(backup.Record.FilePath, cancellationToken: Ct);

        Assert.True(preview.Backup.HasManifest);
        Assert.Equal(5, preview.Contents.PluginConfigCount);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Makes a backup online only on a PC that has never verified it, as on a new PC.</summary>
    private static void MoveToCloud(TestHost host, string path)
    {
        host.Files.OnlineOnly.Add(path);
        host.State.Update(state =>
        {
            state.Verifications.Clear();
            return state;
        });
    }

    /// <summary>An online-only file whose bytes are junk, so reading it would show up as a damaged archive.</summary>
    private static string AddCloudFile(TestHost host, string name)
    {
        Directory.CreateDirectory(host.BackupFolder);
        var path = Path.Combine(host.BackupFolder, name);
        File.WriteAllText(path, "not downloaded");
        host.Files.OnlineOnly.Add(path);
        return path;
    }

    private sealed class Recorded<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }
}

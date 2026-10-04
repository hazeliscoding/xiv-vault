using XivVault.Core.Backup;
using XivVault.Tests.Support;

namespace XivVault.Tests.Core;

public class RetentionTests
{
    [Fact]
    public async Task Keeps_the_newest_regular_backups()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.UpdateConfig(config => config with { RetentionCount = 3 });

        var created = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            created.Add((await host.BackUpAsync(i % 2 == 0 ? BackupKind.Manual : BackupKind.Scheduled)).Record.FilePath);
        }

        var remaining = host.Catalog.List(host.BackupFolder).Select(record => record.FilePath).ToList();
        Assert.Equal(created.AsEnumerable().Reverse().Take(3), remaining);
    }

    [Fact]
    public async Task Retention_reports_what_it_removed()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.UpdateConfig(config => config with { RetentionCount = 1 });

        var first = await host.BackUpAsync();
        var second = await host.BackUpAsync();

        Assert.Equal([first.Record.FilePath], second.RemovedByRetention);
    }

    [Fact]
    public async Task Safety_snapshots_are_kept_separately_from_regular_backups()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.UpdateConfig(config => config with { RetentionCount = 2 });
        var regular = new[] { await host.BackUpAsync(), await host.BackUpAsync() };

        for (var i = 0; i < 5; i++)
        {
            await host.Restores.RestoreAsync(new(regular[1].Record.FilePath), cancellationToken: TestContext.Current.CancellationToken);
            host.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        var records = host.Catalog.List(host.BackupFolder);
        Assert.Equal(3, records.Count(record => record.IsSafetySnapshot));
        Assert.Equal(2, records.Count(record => !record.IsSafetySnapshot));
    }

    [Fact]
    public async Task Damaged_backups_are_neither_counted_nor_deleted()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.UpdateConfig(config => config with { RetentionCount = 2 });
        var oldest = await host.BackUpAsync();
        var damaged = await host.BackUpAsync();
        Corrupt(damaged.Record.FilePath);
        host.Catalog.Verify(host.Catalog.Read(damaged.Record.FilePath)!, TestContext.Current.CancellationToken);

        var newest = await host.BackUpAsync();

        var remaining = host.Catalog.List(host.BackupFolder).Select(record => record.FilePath).ToList();
        Assert.Contains(oldest.Record.FilePath, remaining);
        Assert.Contains(damaged.Record.FilePath, remaining);
        Assert.Contains(newest.Record.FilePath, remaining);
    }

    [Fact]
    public async Task A_backup_damaged_since_it_was_last_checked_does_not_count_as_kept()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.UpdateConfig(config => config with { RetentionCount = 2 });
        var oldest = await host.BackUpAsync();
        var damaged = await host.BackUpAsync();
        Corrupt(damaged.Record.FilePath);

        await host.BackUpAsync();

        Assert.True(File.Exists(oldest.Record.FilePath), "Two good backups must remain, so the oldest good one stays.");
        Assert.True(File.Exists(damaged.Record.FilePath), "Damaged archives are left for the user to inspect.");
        Assert.Equal(IntegrityState.Failed, host.Catalog.Read(damaged.Record.FilePath)!.Integrity);
    }

    [Fact]
    public async Task Other_files_in_the_backup_folder_are_never_touched()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.UpdateConfig(config => config with { RetentionCount = 1 });
        Directory.CreateDirectory(host.BackupFolder);
        var photos = Path.Combine(host.BackupFolder, "photos.zip");
        File.WriteAllText(photos, "not ours");
        var notes = Path.Combine(host.BackupFolder, "notes.txt");
        File.WriteAllText(notes, "not ours");

        await host.BackUpAsync();
        await host.BackUpAsync();

        Assert.True(File.Exists(photos));
        Assert.True(File.Exists(notes));
    }

    /// <summary>Flips bytes inside the stored data of the first payload entry, keeping the ZIP structure readable.</summary>
    internal static void Corrupt(string archivePath)
    {
        var bytes = File.ReadAllBytes(archivePath);
        var marker = System.Text.Encoding.ASCII.GetBytes("payload/");
        var index = bytes.AsSpan().IndexOf(marker);
        var nameLength = BitConverter.ToUInt16(bytes, index - 4);
        var extraLength = BitConverter.ToUInt16(bytes, index - 2);
        var dataStart = index + nameLength + extraLength;
        for (var i = 0; i < 4; i++)
        {
            bytes[dataStart + i] ^= 0xFF;
        }

        var lastWrite = File.GetLastWriteTimeUtc(archivePath);
        File.WriteAllBytes(archivePath, bytes);
        File.SetLastWriteTimeUtc(archivePath, lastWrite.AddSeconds(1));
    }
}

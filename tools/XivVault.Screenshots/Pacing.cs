using XivVault.Core.Backup;
using XivVault.Core.Restore;

namespace XivVault.Screenshots;

/// <summary>
/// Slows a real backup or restore of the fake profile, which takes a fraction of a second, to a
/// pace a recording can show. After each progress report the worker thread waits in proportion to
/// how far the progress moved, so the whole run takes about the same time however often it reports.
/// Work without progress, such as the made-up history, runs at full speed.
/// </summary>
internal sealed class PacedProgress<T>(IProgress<T> inner, Func<T, double> percent, TimeSpan total) : IProgress<T>
{
    private double _last;

    public void Report(T value)
    {
        inner.Report(value);
        var now = percent(value);
        if (now > _last)
        {
            Thread.Sleep(total * ((now - _last) / 100));
            _last = now;
        }
    }
}

internal sealed class PacedBackupService(IBackupService inner, TimeSpan total) : IBackupService
{
    public Task<BackupResult> CreateBackupAsync(BackupRequest request, IProgress<BackupProgress>? progress = null, CancellationToken cancellationToken = default) =>
        inner.CreateBackupAsync(request, progress is null ? null : new PacedProgress<BackupProgress>(progress, value => value.Percent, total), cancellationToken);
}

internal sealed class PacedRestoreService(IRestoreService inner, TimeSpan total) : IRestoreService
{
    public Task<RestorePreview> PreviewAsync(string archivePath, string? source = null, CancellationToken cancellationToken = default) =>
        inner.PreviewAsync(archivePath, source, cancellationToken);

    public Task<IReadOnlyList<SafetyCheck>> CheckAsync(string archivePath, string? source = null, CancellationToken cancellationToken = default) =>
        inner.CheckAsync(archivePath, source, cancellationToken);

    public Task<RestoreResult> RestoreAsync(RestoreRequest request, IProgress<RestoreProgress>? progress = null, CancellationToken cancellationToken = default) =>
        inner.RestoreAsync(request, progress is null ? null : new PacedProgress<RestoreProgress>(progress, value => value.Percent, total), cancellationToken);
}

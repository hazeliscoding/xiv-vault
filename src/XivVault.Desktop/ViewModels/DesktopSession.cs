using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using XivVault.Core;
using XivVault.Core.Backup;
using XivVault.Core.Status;
using XivVault.Desktop.Services;

namespace XivVault.Desktop.ViewModels;

/// <summary>
/// State that outlives any one screen: the latest status from Core, and a running backup, which
/// keeps going (and keeps reporting) while the user moves between screens.
/// </summary>
public sealed partial class DesktopSession(
    IStatusService statusService,
    IBackupService backupService,
    IUiThread uiThread,
    TimeProvider clock,
    ILogger<DesktopSession> logger) : ObservableObject
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public event EventHandler? StatusChanged;

    [ObservableProperty]
    public partial XivVaultStatus? Status { get; private set; }

    [ObservableProperty]
    public partial string? StatusError { get; private set; }

    [ObservableProperty]
    public partial bool IsBackingUp { get; private set; }

    [ObservableProperty]
    public partial bool IsRestoring { get; set; }

    [ObservableProperty]
    public partial BackupProgress? BackupProgress { get; private set; }

    [ObservableProperty]
    public partial BackupResult? LastBackup { get; private set; }

    [ObservableProperty]
    public partial DateTimeOffset? LastBackupAt { get; private set; }

    [ObservableProperty]
    public partial string? BackupError { get; set; }

    /// <summary>True while anything is writing; destructive actions are disabled meanwhile.</summary>
    public bool IsBusy => IsBackingUp || IsRestoring;

    public TimeProvider Clock => clock;

    partial void OnIsBackingUpChanged(bool value) => OnPropertyChanged(nameof(IsBusy));

    partial void OnIsRestoringChanged(bool value) => OnPropertyChanged(nameof(IsBusy));

    public async Task RefreshAsync(bool verifyLatest = true)
    {
        await _refreshGate.WaitAsync();
        try
        {
            Status = await statusService.GetAsync(verifyLatest);
            StatusError = null;
        }
        catch (XivVaultException ex)
        {
            StatusError = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not read the current status");
            StatusError = ex.Message;
        }
        finally
        {
            _refreshGate.Release();
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Back Up Now. Returns false if a backup was already running or it failed.</summary>
    public async Task<bool> BackUpNowAsync()
    {
        if (IsBusy)
        {
            return false;
        }

        IsBackingUp = true;
        BackupError = null;
        BackupProgress = new BackupProgress(BackupStage.Scanning, 0, "Scanning plugin configurations");
        try
        {
            var progress = new UiProgress<BackupProgress>(uiThread, value => BackupProgress = value);
            LastBackup = await backupService.CreateBackupAsync(new BackupRequest(BackupKind.Manual), progress);
            LastBackupAt = clock.GetUtcNow();
            return true;
        }
        catch (XivVaultException ex)
        {
            BackupError = ex.Message;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Backup failed");
            BackupError = ex.Message;
            return false;
        }
        finally
        {
            IsBackingUp = false;
            await RefreshAsync(verifyLatest: false);
        }
    }

    /// <summary>"Just backed up" lasts a few seconds after a backup, like the mockup's completion state.</summary>
    public bool JustBackedUp => LastBackupAt is { } at && clock.GetUtcNow() - at < TimeSpan.FromSeconds(6);
}

/// <summary>Reports progress on the UI thread, in order.</summary>
public sealed class UiProgress<T>(IUiThread uiThread, Action<T> report) : IProgress<T>
{
    public void Report(T value) => uiThread.Post(() => report(value));
}

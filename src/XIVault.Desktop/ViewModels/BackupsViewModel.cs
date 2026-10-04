using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using XIVault.Core;
using XIVault.Core.Backup;
using XIVault.Core.Platform;
using XIVault.Desktop.Services;

namespace XIVault.Desktop.ViewModels;

public enum BackupFilter
{
    All,
    Manual,
    Scheduled,
    Safety,
}

public sealed partial class BackupsViewModel : PageViewModel
{
    private readonly DesktopSession _session;
    private readonly IBackupCatalog _catalog;
    private readonly INavigator _navigator;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly PathDisplay _paths;
    private readonly ILogger<BackupsViewModel> _logger;
    private List<BackupRowViewModel> _all = [];
    private bool _verifying;

    public BackupsViewModel(
        DesktopSession session,
        IBackupCatalog catalog,
        INavigator navigator,
        IDialogService dialogs,
        IShellService shell,
        PathDisplay paths,
        ILogger<BackupsViewModel> logger)
    {
        _session = session;
        _catalog = catalog;
        _navigator = navigator;
        _dialogs = dialogs;
        _shell = shell;
        _paths = paths;
        _logger = logger;
        _session.StatusChanged += (_, _) => Rebuild();
        Filter = Filters[0];
        Rebuild();
    }

    public override AppPage Page => AppPage.Backups;

    public IReadOnlyList<Option<BackupFilter>> Filters { get; } =
    [
        new(BackupFilter.All, "All"),
        new(BackupFilter.Manual, "Manual"),
        new(BackupFilter.Scheduled, "Scheduled"),
        new(BackupFilter.Safety, "Safety"),
    ];

    [ObservableProperty]
    public partial Option<BackupFilter> Filter { get; set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    public ObservableCollection<BackupRowViewModel> Rows { get; } = [];

    public bool IsEmpty => Rows.Count == 0;

    public string EmptyTitle => _all.Count == 0 ? "No backups yet" : "No backups match this filter";

    public string? EmptyDescription => _all.Count == 0
        ? $"Backups appear here once you back up. They are stored in {Destination}."
        : null;

    public string Destination { get; private set; } = "";

    partial void OnFilterChanged(Option<BackupFilter> value) => ApplyFilter();

    public override async Task ActivateAsync()
    {
        await _session.RefreshAsync(verifyLatest: false);
        await VerifyPendingAsync();
    }

    /// <summary>Checks archives this PC has never verified, one at a time, updating each row.</summary>
    public async Task VerifyPendingAsync()
    {
        if (_verifying)
        {
            return;
        }

        _verifying = true;
        try
        {
            foreach (var row in _all.Where(row => row.Record.HasManifest && row.Record.Integrity == IntegrityState.Unverified).ToList())
            {
                row.IsVerifying = true;
                var verified = await Task.Run(() => _catalog.Verify(row.Record));
                row.IsVerifying = false;
                row.Update(verified, _session.Clock.GetLocalNow().DateTime);
            }
        }
        finally
        {
            _verifying = false;
        }
    }

    [RelayCommand]
    private void Restore(BackupRowViewModel row) => _navigator.StartRestore(row.FilePath);

    [RelayCommand]
    private void Inspect(BackupRowViewModel row) => row.IsExpanded = !row.IsExpanded;

    [RelayCommand]
    private void Reveal(BackupRowViewModel row) => _shell.RevealInExplorer(row.FilePath);

    [RelayCommand]
    private async Task DeleteAsync(BackupRowViewModel row)
    {
        ErrorMessage = null;
        if (_session.IsBusy)
        {
            ErrorMessage = "Wait for the current backup or restore to finish before deleting backups.";
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(new ConfirmRequest(
            "Delete this backup?",
            $"The archive is removed from {Destination}. Your current Dalamud configuration is not affected.",
            "Delete backup",
            "Keep backup")
        {
            Detail = $"{row.Day} · {row.Time} · {row.Size} · {Formatting.Count(row.PluginCount, "plugin configuration")}",
            Note = row.FileName,
            Danger = true,
            ConfirmIcon = "Trash2",
        });
        if (!confirmed)
        {
            return;
        }

        try
        {
            _catalog.Delete(row.Record, Path.GetDirectoryName(row.FilePath)!);
        }
        catch (Exception ex) when (ex is XivaultException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete a backup");
            ErrorMessage = $"The backup could not be deleted: {ex.Message}";
        }

        await _session.RefreshAsync(verifyLatest: false);
    }

    private void Rebuild()
    {
        if (_session.Status is not { } status)
        {
            return;
        }

        var now = _session.Clock.GetLocalNow().DateTime;
        Destination = _paths.Friendly(status.Destination);
        var expanded = Rows.Where(row => row.IsExpanded).Select(row => row.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _all = status.Backups.Select(record => new BackupRowViewModel(record, now)
        {
            IsExpanded = expanded.Contains(record.FilePath),
        }).ToList();
        Summary = $"{Formatting.Count(_all.Count, "backup")} · {Formatting.Bytes(status.TotalBytes)} total · keeping latest {status.Config.RetentionCount}";
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filter = Filter?.Value ?? BackupFilter.All;
        Rows.Clear();
        foreach (var row in _all.Where(row => filter switch
        {
            BackupFilter.Manual => row.Record.Kind == BackupKind.Manual,
            BackupFilter.Scheduled => row.Record.Kind == BackupKind.Scheduled,
            BackupFilter.Safety => row.Record.Kind == BackupKind.PreRestore,
            _ => true,
        }))
        {
            Rows.Add(row);
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDescription));
    }
}

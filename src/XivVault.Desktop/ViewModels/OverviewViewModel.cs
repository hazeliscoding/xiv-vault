using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XivVault.Core;
using XivVault.Core.Backup;
using XivVault.Core.Platform;
using XivVault.Core.State;
using XivVault.Core.Status;
using XivVault.Desktop.Controls;
using XivVault.Desktop.Services;

namespace XivVault.Desktop.ViewModels;

public enum OverviewState
{
    Loading,
    NotDetected,
    NoConfiguration,
    Empty,
    Protected,
    BackingUp,
    JustBackedUp,
    NeedsAttention,
}

public sealed partial class OverviewViewModel : PageViewModel
{
    public const string PluginConfigsExplanation =
        "Per-plugin settings stored by Dalamud in pluginConfigs. Plugin binaries themselves are not included.";

    private readonly DesktopSession _session;
    private readonly INavigator _navigator;
    private readonly PathDisplay _paths;
    private readonly IUiThread _uiThread;

    public OverviewViewModel(DesktopSession session, INavigator navigator, PathDisplay paths, IUiThread uiThread)
    {
        _session = session;
        _uiThread = uiThread;
        _navigator = navigator;
        _paths = paths;
        _session.StatusChanged += (_, _) => Rebuild();
        _session.PropertyChanged += OnSessionChanged;
        Rebuild();
    }

    public override AppPage Page => AppPage.Overview;

    [ObservableProperty]
    public partial OverviewState State { get; private set; }

    [ObservableProperty]
    public partial string Subtitle { get; private set; } = "Checking your Dalamud setup…";

    [ObservableProperty]
    public partial Tone BadgeTone { get; private set; }

    [ObservableProperty]
    public partial string BadgeLabel { get; private set; } = "";

    [ObservableProperty]
    public partial string LastBackupLabel { get; private set; } = "";

    [ObservableProperty]
    public partial string PluginCount { get; private set; } = "";

    [ObservableProperty]
    public partial string CompressedSize { get; private set; } = "";

    [ObservableProperty]
    public partial string IntegrityLabel { get; private set; } = "";

    [ObservableProperty]
    public partial Tone IntegrityTone { get; private set; }

    [ObservableProperty]
    public partial string Destination { get; private set; } = "";

    [ObservableProperty]
    public partial string LauncherPath { get; private set; } = "%AppData%\\XIVLauncher";

    [ObservableProperty]
    public partial string EmptyDescription { get; private set; } = "";

    [ObservableProperty]
    public partial string NotDetectedDescription { get; private set; } = "";

    [ObservableProperty]
    public partial string? FailedDescription { get; private set; }

    [ObservableProperty]
    public partial string? FailedMeta { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    public partial string StageLabel { get; private set; } = "";

    [ObservableProperty]
    public partial double Percent { get; private set; }

    [ObservableProperty]
    public partial string PercentLabel { get; private set; } = "";

    public ObservableCollection<StatusItem> StatusRow { get; } = [];

    public ObservableCollection<BackupRowViewModel> Recent { get; } = [];

    public bool ShowStats => State is OverviewState.Protected or OverviewState.JustBackedUp or OverviewState.NeedsAttention;

    public bool ShowRunning => State == OverviewState.BackingUp;

    public bool ShowEmpty => State == OverviewState.Empty;

    public bool ShowNotDetected => State == OverviewState.NotDetected;

    public bool ShowNoConfiguration => State == OverviewState.NoConfiguration;

    public bool ShowFailed => State == OverviewState.NeedsAttention && FailedDescription is not null;

    public bool ShowAttention => State == OverviewState.NeedsAttention && FailedDescription is null;

    public bool HasRecent => Recent.Count > 0;

    public bool IsBusy => _session.IsBusy;

    partial void OnStateChanged(OverviewState value)
    {
        foreach (var name in new[] { nameof(ShowStats), nameof(ShowRunning), nameof(ShowEmpty), nameof(ShowNotDetected), nameof(ShowNoConfiguration), nameof(ShowFailed), nameof(ShowAttention) })
        {
            OnPropertyChanged(name);
        }
    }

    public override Task ActivateAsync() => _session.RefreshAsync();

    [RelayCommand]
    private async Task BackUpNowAsync()
    {
        ErrorMessage = null;
        var ok = await _session.BackUpNowAsync();
        if (!ok && _session.BackupError is { } error)
        {
            ErrorMessage = error;
        }

        Rebuild();
        if (ok)
        {
            // The completion state is shown briefly, then the card settles back to "Protected".
            _ = Task.Delay(TimeSpan.FromSeconds(6.2)).ContinueWith(_ => _uiThread.Post(Rebuild), TaskScheduler.Default);
        }
    }

    [RelayCommand]
    private void Restore() => _navigator.StartRestore();

    [RelayCommand]
    private void AllBackups() => _navigator.Navigate(AppPage.Backups);

    [RelayCommand]
    private void OpenSettings() => _navigator.Navigate(AppPage.Settings);

    [RelayCommand]
    private Task RetryAsync() => _session.RefreshAsync();

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DesktopSession.BackupProgress):
                UpdateProgress();
                break;
            case nameof(DesktopSession.IsBackingUp):
            case nameof(DesktopSession.IsRestoring):
                OnPropertyChanged(nameof(IsBusy));
                Rebuild();
                break;
        }
    }

    private void UpdateProgress()
    {
        if (_session.BackupProgress is not { } progress)
        {
            return;
        }

        StageLabel = progress.Message;
        Percent = progress.Percent;
        PercentLabel = string.Create(CultureInfo.InvariantCulture, $"{progress.Percent:0}% · Destination {Destination}");
    }

    private void Rebuild()
    {
        var status = _session.Status;
        if (_session.IsBackingUp)
        {
            State = OverviewState.BackingUp;
            Subtitle = "Backing up your Dalamud setup…";
            UpdateProgress();
            return;
        }

        if (status is null)
        {
            State = OverviewState.Loading;
            if (_session.StatusError is { } error)
            {
                ErrorMessage = error;
            }

            return;
        }

        var now = _session.Clock.GetLocalNow().DateTime;
        Destination = _paths.Friendly(status.Destination);
        if (status.Launcher.Installation is { } installation)
        {
            LauncherPath = _paths.Friendly(installation.RootPath);
        }

        BuildStatusRow(status);
        BuildRecent(status, now);

        var latest = status.LatestBackup;
        if (latest is not null)
        {
            LastBackupLabel = _session.JustBackedUp ? "Just now" : Formatting.DayAndTime(latest.CreatedAtUtc.ToLocalTime(), now);
            PluginCount = latest.HasManifest ? latest.PluginConfigCount.ToString(CultureInfo.InvariantCulture) : "–";
            CompressedSize = Formatting.Bytes(latest.SizeBytes);
            IntegrityLabel = Formatting.IntegrityLabel(latest);
            IntegrityTone = BackupRowViewModel.IntegrityToneOf(latest);
        }

        FailedDescription = null;
        FailedMeta = null;
        switch (status.State)
        {
            case ProtectionState.NotDetected:
                State = OverviewState.NotDetected;
                Subtitle = "XIVLauncher was not found on this PC.";
                NotDetectedDescription = "Install XIVLauncher and start it once, or point XIV Vault at its folder in Settings. Your backups are safe either way.";
                break;
            case ProtectionState.NoConfiguration:
                State = OverviewState.NoConfiguration;
                Subtitle = "XIVLauncher is installed, but Dalamud has no settings on this PC yet.";
                break;
            case ProtectionState.NotBackedUp:
                State = OverviewState.Empty;
                Subtitle = "Your Dalamud setup is not backed up yet.";
                var count = status.Portable?.PluginConfigCount ?? 0;
                EmptyDescription = $"{Formatting.Count(count, "plugin configuration")} and your Dalamud settings were found. "
                    + $"The first backup takes a few seconds and is stored at {Destination}.";
                break;
            case ProtectionState.NeedsAttention:
                State = OverviewState.NeedsAttention;
                Subtitle = "Your latest backup needs attention.";
                BadgeTone = Tone.Warning;
                BadgeLabel = "Needs attention";
                if (latest is { Integrity: IntegrityState.Failed })
                {
                    var previous = status.Backups.FirstOrDefault(record => record.IsRegular
                        && record.Integrity == IntegrityState.Verified && record.FilePath != latest.FilePath);
                    FailedDescription = (latest.Problem ?? "A file in the archive does not match its recorded hash.")
                        + (previous is null
                            ? " Back up again to replace it."
                            : $" The backup from {Formatting.DayAndTime(previous.CreatedAtUtc.ToLocalTime(), now)} is intact and will be used for restores until a new backup succeeds.");
                    FailedMeta = latest.FileName;
                }
                else
                {
                    FailedDescription = null;
                    Subtitle = status.AttentionReason ?? Subtitle;
                }

                break;
            default:
                var just = _session.JustBackedUp;
                State = just ? OverviewState.JustBackedUp : OverviewState.Protected;
                Subtitle = just ? "Backup complete. Your Dalamud setup is protected." : "Your Dalamud setup is protected.";
                BadgeTone = Tone.Healthy;
                BadgeLabel = just ? "Backed up" : "Protected";
                break;
        }

        OnPropertyChanged(nameof(ShowFailed));
        OnPropertyChanged(nameof(ShowAttention));
    }

    private void BuildStatusRow(XivVaultStatus status)
    {
        StatusRow.Clear();
        StatusRow.Add(status.Launcher.IsFound
            ? new StatusItem(Tone.Healthy, "XIVLauncher detected")
            : new StatusItem(Tone.Critical, "XIVLauncher not found"));
        StatusRow.Add(status.Launcher.Installation is { HasPortableConfiguration: true }
            ? new StatusItem(Tone.Healthy, "Dalamud configuration found")
            : new StatusItem(Tone.Warning, "No Dalamud configuration yet"));
        StatusRow.Add(status.AutomaticBackupsEnabled
            ? new StatusItem(Tone.Healthy, "Automatic backups enabled")
            : new StatusItem(Tone.Paused, "Automatic backups off"));
        StatusRow.Add(status.Schedule.LastRun switch
        {
            { Result: ScheduledRunResult.Success } => new StatusItem(Tone.Healthy, "Last scheduled backup successful"),
            { Result: ScheduledRunResult.Skipped } => new StatusItem(Tone.Warning, "Last scheduled backup skipped"),
            { Result: ScheduledRunResult.Failed } => new StatusItem(Tone.Critical, "Last scheduled backup failed"),
            _ => new StatusItem(Tone.Unknown, "No scheduled backup yet"),
        });
    }

    private void BuildRecent(XivVaultStatus status, DateTime now)
    {
        Recent.Clear();
        foreach (var record in status.Backups.Where(record => record.IsRegular).Take(3))
        {
            Recent.Add(new BackupRowViewModel(record, now));
        }

        OnPropertyChanged(nameof(HasRecent));
    }
}

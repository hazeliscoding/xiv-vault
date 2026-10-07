using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XivVault.Core;
using XivVault.Core.Backup;
using XivVault.Core.Platform;
using XivVault.Core.Restore;
using XivVault.Desktop.Controls;
using XivVault.Desktop.Services;

namespace XivVault.Desktop.ViewModels;

public enum StepState
{
    Todo,
    Current,
    Done,
}

public sealed record StepItem(int Number, string Label, StepState State, bool HasLine)
{
    public string Mark => State == StepState.Done ? "✓" : Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record ContentItem(bool Included, string Label);

public sealed record CurrentItem(string Label, string Meta);

public sealed record CheckItem(Tone Tone, string Label, string Detail);

public sealed record StageItem(StepState State, string Label)
{
    public string Mark => State switch
    {
        StepState.Done => "✓",
        StepState.Current => "›",
        _ => "·",
    };
}

/// <summary>Choose → Review → Safety check → Complete. Nothing on disk changes before the last step.</summary>
public sealed partial class RestoreViewModel : PageViewModel
{
    private static readonly string[] StepNames = ["Choose backup", "Review", "Safety check", "Complete"];

    private readonly DesktopSession _session;
    private readonly IBackupCatalog _catalog;
    private readonly IRestoreService _restores;
    private readonly IFilePicker _picker;
    private readonly IShellService _shell;
    private readonly INavigator _navigator;
    private readonly PathDisplay _paths;
    private readonly IUiThread _uiThread;
    private readonly IMotionSettings _motion;
    private readonly List<string> _extraFiles = [];
    private string? _pendingSelection;
    private CancellationTokenSource? _download;

    // Until the user picks a backup, the newest one is selected, including one made after the
    // wizard was built. A backup the user picked stays selected as new ones appear.
    private bool _userChose;
    private bool _rebuilding;

    public RestoreViewModel(
        DesktopSession session,
        IBackupCatalog catalog,
        IRestoreService restores,
        IFilePicker picker,
        IShellService shell,
        INavigator navigator,
        PathDisplay paths,
        IUiThread uiThread,
        IMotionSettings motion)
    {
        _session = session;
        _catalog = catalog;
        _restores = restores;
        _picker = picker;
        _shell = shell;
        _navigator = navigator;
        _paths = paths;
        _uiThread = uiThread;
        _motion = motion;
        _session.StatusChanged += (_, _) => RebuildChoices();
        UpdateSteps();
        RebuildChoices();
    }

    public override AppPage Page => AppPage.Restore;

    [ObservableProperty]
    public partial int Step { get; private set; } = 1;

    [ObservableProperty]
    public partial BackupRowViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial RestorePreview? Preview { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool IsDownloading { get; private set; }

    [ObservableProperty]
    public partial string DownloadText { get; private set; } = "";

    [ObservableProperty]
    public partial bool ChecksPassed { get; private set; }

    [ObservableProperty]
    public partial bool IsRestoring { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    public partial RestoreResult? Result { get; private set; }

    public ObservableCollection<StepItem> Steps { get; } = [];

    public ObservableCollection<BackupRowViewModel> Choices { get; } = [];

    public ObservableCollection<ContentItem> Contents { get; } = [];

    public ObservableCollection<CurrentItem> Current { get; } = [];

    public ObservableCollection<CheckItem> Checks { get; } = [];

    public ObservableCollection<StageItem> Stages { get; } = [];

    public bool IsStep1 => Step == 1;

    public bool IsStep2 => Step == 2;

    public bool IsStep3 => Step == 3;

    public bool IsStep4 => Step == 4;

    public bool HasChoices => Choices.Count > 0;

    public string Destination { get; private set; } = "";

    public string SelectedTitle => Selected?.FullDate ?? "";

    public bool SelectedIsInCloud => Selected is { Record.IsOnlineOnly: true };

    public bool ShowCloudNote => SelectedIsInCloud && !IsDownloading;

    public string CloudNoteText => Selected is { } row
        ? $"It downloads first ({row.Size}) so XIV Vault can show what it holds. Your settings stay as they are until the last step."
        : "";

    public string ContinueLabel => SelectedIsInCloud ? "Download and continue" : "Continue";

    public bool ShowOlderWarning => Preview is { IsOlderThanCurrent: true };

    public string OlderWarningText
    {
        get
        {
            if (Preview is not { } preview)
            {
                return "";
            }

            var plugins = preview.PluginsChangedSinceBackup;
            return plugins.Count > 0
                ? $"{Formatting.Count(plugins.Count, "plugin")} configured after this backup ({Formatting.JoinWords(plugins.Take(4))}) will return to {(plugins.Count == 1 ? "its" : "their")} earlier settings. The safety backup keeps today's values recoverable."
                : "Your Dalamud settings changed after this backup and will return to their earlier values. The safety backup keeps today's values recoverable.";
        }
    }

    public string? TargetProblem => Preview?.TargetProblem;

    public bool CanContinueReview => Preview is { Target: not null };

    public string RestoreSummary => Selected is { } row
        ? $"Restores {Formatting.Count(row.PluginCount, "plugin configuration")} from {row.Day}, {row.Time}"
        : "";

    public bool CanRestore => ChecksPassed && !IsRestoring && !_session.IsBackingUp;

    public bool CanGoBack => !IsRestoring;

    public string ResultPlugins => Result is { } result ? $"{Formatting.Count(result.PluginConfigCount, "plugin configuration")} restored" : "";

    public string ResultDalamud => Result is { DalamudConfigRestored: true } ? "Dalamud configuration restored" : "Dalamud configuration unchanged (not in this backup)";

    public string ResultSnapshot => Result is { } result
        ? $"Safety snapshot created · {Formatting.DayAndTime(result.SafetySnapshot.CreatedAtUtc.ToLocalTime(), _session.Clock.GetLocalNow().DateTime)}"
        : "";

    public string ResultSnapshotFile => Result is { } result
        ? $"{result.SafetySnapshot.FileName} in {_paths.Friendly(Path.GetDirectoryName(result.SafetySnapshot.FilePath)!)}"
        : "";

    public bool CanOpenLauncher => Preview?.Target?.LauncherExecutable is { } exe && File.Exists(exe);

    partial void OnStepChanged(int value)
    {
        UpdateSteps();
        foreach (var name in new[] { nameof(IsStep1), nameof(IsStep2), nameof(IsStep3), nameof(IsStep4) })
        {
            OnPropertyChanged(name);
        }
    }

    partial void OnSelectedChanged(BackupRowViewModel? value)
    {
        if (!_rebuilding)
        {
            _userChose = true;
        }

        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(RestoreSummary));
        NotifyCloudChanged();
        ContinueCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsDownloadingChanged(bool value) => OnPropertyChanged(nameof(ShowCloudNote));

    partial void OnPreviewChanged(RestorePreview? value)
    {
        OnPropertyChanged(nameof(ShowOlderWarning));
        OnPropertyChanged(nameof(OlderWarningText));
        OnPropertyChanged(nameof(TargetProblem));
        OnPropertyChanged(nameof(CanContinueReview));
        OnPropertyChanged(nameof(CanOpenLauncher));
        ContinueCommand.NotifyCanExecuteChanged();
    }

    partial void OnChecksPassedChanged(bool value) => RefreshRestoreState();

    partial void OnIsRestoringChanged(bool value)
    {
        RefreshRestoreState();
        OnPropertyChanged(nameof(CanGoBack));
        BackCommand.NotifyCanExecuteChanged();
    }

    partial void OnResultChanged(RestoreResult? value)
    {
        OnPropertyChanged(nameof(ResultPlugins));
        OnPropertyChanged(nameof(ResultDalamud));
        OnPropertyChanged(nameof(ResultSnapshot));
        OnPropertyChanged(nameof(ResultSnapshotFile));
    }

    /// <summary>Opens the wizard on step 1 with this backup chosen, e.g. from a Backups row.</summary>
    public void Begin(string? backupPath)
    {
        if (IsRestoring)
        {
            return;
        }

        // A download for the backup chosen before stops; the new choice is applied when it has.
        _download?.Cancel();
        Step = 1;
        ErrorMessage = null;
        Result = null;
        _userChose = false;
        _pendingSelection = backupPath;
        RebuildChoices();
    }

    public override async Task ActivateAsync()
    {
        if (Step == 1)
        {
            await _session.RefreshAsync(verifyLatest: false);
        }
    }

    private bool CanContinue() => Step switch
    {
        1 => Selected is not null && !IsLoading,
        2 => CanContinueReview && !IsLoading,
        _ => false,
    };

    [RelayCommand(CanExecute = nameof(CanContinue))]
    private async Task ContinueAsync()
    {
        ErrorMessage = null;
        if (Step == 1 && Selected is { } row)
        {
            IsLoading = true;
            try
            {
                if (row.Record.IsOnlineOnly)
                {
                    await DownloadAsync(row);
                }

                Preview = await _restores.PreviewAsync(row.FilePath);
                BuildReview(Preview);
                Step = 2;
            }
            catch (OperationCanceledException)
            {
                // The user stopped the download. Nothing else happened, so there is nothing to report.
            }
            catch (Exception ex) when (ex is XivVaultException or IOException or UnauthorizedAccessException)
            {
                ErrorMessage = ex.Message;
            }
            finally
            {
                IsLoading = false;
                if (Step == 1)
                {
                    RebuildChoices();
                }
            }
        }
        else if (Step == 2)
        {
            Step = 3;
            await RunChecksAsync();
        }
    }

    [RelayCommand]
    private void CancelDownload() => _download?.Cancel();

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        ErrorMessage = null;
        if (Step > 1 && Step < 4)
        {
            Step--;
        }
    }

    [RelayCommand]
    private async Task RunChecksAsync()
    {
        if (Selected is not { } row)
        {
            return;
        }

        ChecksPassed = false;
        IsLoading = true;
        Checks.Clear();
        Stages.Clear();
        try
        {
            var checks = await _restores.CheckAsync(row.FilePath);
            foreach (var check in checks)
            {
                if (!_motion.ReduceMotion)
                {
                    // The checks arrive one by one, so each result can be read as it lands.
                    await Task.Delay(140);
                }

                Checks.Add(new CheckItem(check.Passed ? Tone.Healthy : Tone.Critical, check.Label, check.Detail));
            }

            ChecksPassed = checks.All(check => check.Passed);
            if (!ChecksPassed)
            {
                ErrorMessage = checks.First(check => !check.Passed) switch
                {
                    { Id: SafetyCheckId.XivLauncherClosed or SafetyCheckId.GameClosed } => "Close XIVLauncher and FFXIV, then run the checks again.",
                    { Id: SafetyCheckId.IntegrityVerified } => "This backup did not pass verification, so it can't be restored. Choose another backup.",
                    { Id: SafetyCheckId.DestinationAvailable } => "The XIVLauncher folder isn't available. Check the folder in Settings.",
                    _ => "The backup folder isn't available for the safety snapshot. Reconnect the drive or choose another folder in Settings.",
                };
            }
        }
        catch (Exception ex) when (ex is XivVaultException or IOException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RestoreNowAsync()
    {
        if (Selected is not { } row || !CanRestore)
        {
            return;
        }

        ErrorMessage = null;
        IsRestoring = true;
        _session.IsRestoring = true;
        SetStage(RestoreStage.VerifyingIntegrity, row.PluginCount);
        try
        {
            var progress = new UiProgress<RestoreProgress>(_uiThread, value => SetStage(value.Stage, row.PluginCount));
            Result = await _restores.RestoreAsync(new RestoreRequest(row.FilePath), progress);
            SetStage(RestoreStage.Completed, row.PluginCount);
            Step = 4;
        }
        catch (XivVaultException ex)
        {
            ErrorMessage = ex.Message;
            ChecksPassed = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = $"The restore stopped: {ex.Message}";
            ChecksPassed = false;
        }
        finally
        {
            IsRestoring = false;
            _session.IsRestoring = false;
            await _session.RefreshAsync(verifyLatest: false);
        }
    }

    [RelayCommand]
    private void StartOver() => Begin(null);

    [RelayCommand]
    private async Task ChooseFileAsync()
    {
        var file = await _picker.PickBackupFileAsync(_session.Status?.Destination);
        if (file is null)
        {
            return;
        }

        var record = _catalog.Read(file);
        if (record is not { IsRecognized: true })
        {
            ErrorMessage = $"{Path.GetFileName(file)} is not a XIV Vault backup.";
            return;
        }

        if (!_extraFiles.Contains(record.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            _extraFiles.Add(record.FilePath);
        }

        _pendingSelection = record.FilePath;
        RebuildChoices();
    }

    [RelayCommand]
    private void OpenLauncher()
    {
        if (Preview?.Target?.LauncherExecutable is { } exe && !_shell.Launch(exe))
        {
            ErrorMessage = "XIVLauncher could not be started. Open it from the Start menu.";
        }
    }

    [RelayCommand]
    private void ViewBackups() => _navigator.Navigate(AppPage.Backups);

    [RelayCommand]
    private void OpenSettings() => _navigator.Navigate(AppPage.Settings);

    private async Task DownloadAsync(BackupRowViewModel row)
    {
        using var download = new CancellationTokenSource();
        _download = download;
        var size = row.Size;
        DownloadText = $"0 B of {size}";
        IsDownloading = true;
        try
        {
            var progress = new UiProgress<long>(_uiThread, read => DownloadText = $"{Formatting.Bytes(read)} of {size}");
            var record = await Task.Run(() => _catalog.Download(row.Record, progress, download.Token), download.Token);

            // The list may have been rebuilt while the file downloaded, so update every row showing it.
            var now = _session.Clock.GetLocalNow().DateTime;
            foreach (var choice in Choices.Append(row).Where(choice => string.Equals(choice.FilePath, record.FilePath, StringComparison.OrdinalIgnoreCase)))
            {
                choice.Update(record, now);
            }

            NotifyCloudChanged();
            OnPropertyChanged(nameof(RestoreSummary));
        }
        finally
        {
            _download = null;
            IsDownloading = false;
        }
    }

    private void NotifyCloudChanged()
    {
        OnPropertyChanged(nameof(SelectedIsInCloud));
        OnPropertyChanged(nameof(ShowCloudNote));
        OnPropertyChanged(nameof(CloudNoteText));
        OnPropertyChanged(nameof(ContinueLabel));
    }

    private void RefreshRestoreState()
    {
        OnPropertyChanged(nameof(CanRestore));
        RestoreNowCommand.NotifyCanExecuteChanged();
    }

    private void SetStage(RestoreStage stage, int pluginCount)
    {
        var current = stage switch
        {
            RestoreStage.VerifyingIntegrity => 0,
            RestoreStage.CreatingSafetySnapshot => 1,
            RestoreStage.Extracting or RestoreStage.RestoringPluginConfigs => 2,
            RestoreStage.RestoringDalamudSettings => 3,
            _ => 4,
        };
        string[] labels =
        [
            "Verifying backup integrity",
            "Creating pre-restore safety backup",
            $"Restoring {Formatting.Count(pluginCount, "plugin configuration")}",
            "Restoring Dalamud settings",
        ];
        Stages.Clear();
        for (var i = 0; i < labels.Length; i++)
        {
            Stages.Add(new StageItem(i < current ? StepState.Done : i == current ? StepState.Current : StepState.Todo, labels[i]));
        }
    }

    private void UpdateSteps()
    {
        Steps.Clear();
        for (var i = 0; i < StepNames.Length; i++)
        {
            var number = i + 1;
            var state = number < Step ? StepState.Done : number == Step ? StepState.Current : StepState.Todo;
            Steps.Add(new StepItem(number, StepNames[i], state, number < StepNames.Length));
        }
    }

    private void RebuildChoices()
    {
        // While a backup downloads or loads, a refresh must not swap the selection under it: the
        // review would describe one backup and the restore use another.
        if (IsRestoring || IsLoading || Step != 1)
        {
            return;
        }

        var now = _session.Clock.GetLocalNow().DateTime;
        var keep = _pendingSelection ?? (_userChose ? Selected?.FilePath : null);
        _userChose |= _pendingSelection is not null;
        var records = (_session.Status?.Backups ?? []).Where(record => record.IsRecognized).ToList();
        foreach (var extra in _extraFiles)
        {
            if (!records.Any(record => string.Equals(record.FilePath, extra, StringComparison.OrdinalIgnoreCase)) && _catalog.Read(extra) is { } record)
            {
                records.Insert(0, record);
            }
        }

        Destination = _session.Status is { } status ? _paths.Friendly(status.Destination) : "";
        _rebuilding = true;
        try
        {
            Choices.Clear();
            foreach (var record in records)
            {
                Choices.Add(new BackupRowViewModel(record, now));
            }

            Selected = Choices.FirstOrDefault(choice => string.Equals(choice.FilePath, keep, StringComparison.OrdinalIgnoreCase))
                ?? Choices.FirstOrDefault(choice => !choice.IsSafety && choice.CanRestore)
                ?? Choices.FirstOrDefault();
        }
        finally
        {
            _rebuilding = false;
        }

        _pendingSelection = null;
        OnPropertyChanged(nameof(HasChoices));
        OnPropertyChanged(nameof(Destination));
    }

    private void BuildReview(RestorePreview preview)
    {
        var now = _session.Clock.GetLocalNow().DateTime;
        var contents = preview.Contents;
        Contents.Clear();
        Contents.Add(new ContentItem(true, Formatting.Count(contents.PluginConfigCount, "plugin configuration")));
        Contents.Add(new ContentItem(contents.DalamudConfig, "Dalamud settings"));
        Contents.Add(new ContentItem(contents.DalamudVfs, "Plugin collection database"));
        Contents.Add(new ContentItem(contents.DalamudConfig, contents.CustomRepositoryCount is { } repos and > 0
            ? $"Custom repository settings · {Formatting.Count(repos, "repo")}"
            : "Custom repository settings"));
        Contents.Add(new ContentItem(contents.DalamudUi, contents.DalamudUi ? "UI layout" : "UI layout — not included"));

        Current.Clear();
        if (preview.Current is { } current)
        {
            string Changed(DateTime? utc) => utc is { } value ? "changed " + Formatting.Day(value.ToLocalTime(), now).Replace("Today", "today", StringComparison.Ordinal).Replace("Yesterday", "yesterday", StringComparison.Ordinal) : "not set up";
            Current.Add(new CurrentItem(Formatting.Count(current.PluginConfigCount, "plugin configuration"), Changed(current.PluginConfigsChangedUtc)));
            Current.Add(new CurrentItem("Dalamud settings", current.DalamudConfig ? Changed(current.DalamudConfigChangedUtc) : "not set up"));
            Current.Add(new CurrentItem("Plugin collection database", current.DalamudVfs ? Changed(current.DalamudVfsChangedUtc) : "not set up"));
            Current.Add(new CurrentItem("Custom repository settings", current.CustomRepositoryCount is { } currentRepos ? Formatting.Count(currentRepos, "repo") : "none"));
            Current.Add(new CurrentItem("UI layout", contents.DalamudUi ? "will be replaced" : "kept as is"));
        }
    }
}

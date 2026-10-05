using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using XivVault.Core;
using XivVault.Core.Configuration;
using XivVault.Core.Platform;
using XivVault.Desktop.Controls;
using XivVault.Desktop.Services;

namespace XivVault.Desktop.ViewModels;

public enum UpdateState
{
    Portable,
    NotChecked,
    Checking,
    UpToDate,
    CheckFailed,
    Available,
    Downloading,
}

/// <summary>
/// The update check and install, shared by Settings and the navigation footer. Updates are found
/// automatically but only installed when the user chooses, and never while anything is writing.
/// </summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    public const string ReleasesUrl = XivVaultInfo.RepositoryUrl + "/releases";

    private readonly IAppUpdater _updater;
    private readonly IAppInstances _instances;
    private readonly OperationLock _operationLock;
    private readonly DesktopSession _session;
    private readonly IConfigStore _configStore;
    private readonly IShellService _shell;
    private readonly IUiThread _uiThread;
    private readonly ILogger<UpdatesViewModel> _logger;

    public UpdatesViewModel(
        IAppUpdater updater,
        IAppInstances instances,
        OperationLock operationLock,
        DesktopSession session,
        IConfigStore configStore,
        IShellService shell,
        IUiThread uiThread,
        ILogger<UpdatesViewModel> logger)
    {
        _updater = updater;
        _instances = instances;
        _operationLock = operationLock;
        _session = session;
        _configStore = configStore;
        _shell = shell;
        _uiThread = uiThread;
        _logger = logger;
        State = updater.IsInstalled ? UpdateState.NotChecked : UpdateState.Portable;
    }

    public bool IsInstalled => _updater.IsInstalled;

    public string VersionText { get; } = "XIV Vault " + XivVaultInfo.Version;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail), nameof(BadgeLabel), nameof(BadgeTone), nameof(HasUpdate), nameof(CanCheck), nameof(IsChecking), nameof(IsDownloading))]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand), nameof(UpdateAndRestartCommand))]
    public partial UpdateState State { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail), nameof(NavLabel))]
    public partial AvailableUpdate? Update { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    public partial int DownloadPercent { get; private set; }

    /// <summary>Why the last action didn't go ahead, shown under the version.</summary>
    [ObservableProperty]
    public partial string? Notice { get; private set; }

    public bool HasUpdate => State is UpdateState.Available or UpdateState.Downloading;

    public bool CanCheck => IsInstalled && !HasUpdate;

    public bool IsChecking => State == UpdateState.Checking;

    public bool IsDownloading => State == UpdateState.Downloading;

    public string NavLabel => Update is { } update ? $"Version {update.Version} available" : "";

    public string Detail => State switch
    {
        UpdateState.Portable => "This copy runs from a zip file, so it can't update itself. Install XIV Vault with Setup to get updates, or download new versions from GitHub.",
        UpdateState.Checking => "Checking GitHub for a new version.",
        UpdateState.UpToDate => "You have the latest version.",
        UpdateState.CheckFailed => "XIV Vault could not reach GitHub to check for updates. Check your internet connection and try again.",
        UpdateState.Available => $"Version {Update?.Version} is ready to install. XIV Vault closes, updates and opens again. Your settings and backups stay as they are.",
        UpdateState.Downloading => $"Downloading version {Update?.Version}: {DownloadPercent}%.",
        _ => "Select Check Now to look for a new version on GitHub.",
    };

    public string? BadgeLabel => State switch
    {
        UpdateState.Portable => "Portable",
        UpdateState.UpToDate => "Up to date",
        UpdateState.CheckFailed => "Not checked",
        UpdateState.Available or UpdateState.Downloading => "Update available",
        _ => null,
    };

    public Tone BadgeTone => State switch
    {
        UpdateState.UpToDate => Tone.Healthy,
        UpdateState.CheckFailed => Tone.Warning,
        UpdateState.Available or UpdateState.Downloading => Tone.Accent,
        _ => Tone.Unknown,
    };

    /// <summary>The check when the app opens: only for installed copies, and only if it is turned on.</summary>
    public async Task CheckOnStartupAsync()
    {
        if (!IsInstalled)
        {
            return;
        }

        try
        {
            if (!_configStore.Load().CheckForUpdates)
            {
                return;
            }
        }
        catch (Exception ex) when (ex is XivVaultException or IOException or UnauthorizedAccessException)
        {
            // An unreadable settings file is reported by Settings; it isn't a reason to go online.
            _logger.LogWarning(ex, "Skipped the update check because the settings could not be read");
            return;
        }

        await CheckNowAsync();
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckNowAsync()
    {
        State = UpdateState.Checking;
        Notice = null;
        try
        {
            Update = await _updater.CheckAsync();
            State = Update is null ? UpdateState.UpToDate : UpdateState.Available;
        }
        catch (Exception ex)
        {
            // Offline, rate-limited or a missing feed all mean the same thing: no answer this time.
            _logger.LogWarning(ex, "Could not check for updates");
            State = UpdateState.CheckFailed;
        }
    }

    private bool CanUpdate => State == UpdateState.Available;

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task UpdateAndRestartAsync()
    {
        if (Update is not { } update)
        {
            return;
        }

        if (WhyNotNow() is { } blocked)
        {
            Notice = blocked;
            return;
        }

        Notice = null;
        State = UpdateState.Downloading;
        DownloadPercent = 0;
        try
        {
            await _updater.DownloadAsync(update, percent => _uiThread.Post(() => DownloadPercent = percent));
        }
        catch (Exception ex)
        {
            // A failed download leaves the installed version untouched, so trying again is safe.
            _logger.LogWarning(ex, "Could not download version {Version}", update.Version);
            State = UpdateState.Available;
            Notice = "The update could not be downloaded. Check your internet connection and try again.";
            return;
        }

        // Something may have started while the update downloaded.
        if (WhyNotNow() is { } after)
        {
            State = UpdateState.Available;
            Notice = after;
            return;
        }

        _logger.LogInformation("Restarting to install version {Version}", update.Version);
        _updater.RestartToApply(update);
    }

    [RelayCommand]
    private void OpenReleases() => _shell.OpenUrl(ReleasesUrl);

    /// <summary>
    /// Installing ends every copy of XIV Vault that runs from the install folder. A backup it
    /// interrupts would never finish, so the update waits until nothing is working.
    /// </summary>
    private string? WhyNotNow()
    {
        if (_session.IsBusy)
        {
            return "Wait for the backup or restore to finish, then update.";
        }

        if (_instances.OthersRunning)
        {
            return "XIV Vault is also running somewhere else, for example a scheduled backup. Update when it has finished, or close the other window.";
        }

        try
        {
            _operationLock.Acquire(TimeSpan.Zero).Dispose();
        }
        catch (XivVaultException)
        {
            return "A backup or restore is running. Update when it has finished.";
        }

        return null;
    }
}

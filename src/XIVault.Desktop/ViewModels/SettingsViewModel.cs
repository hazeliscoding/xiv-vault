using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XIVault.Core;
using XIVault.Core.Configuration;
using XIVault.Core.Discovery;
using XIVault.Core.Platform;
using XIVault.Desktop.Controls;
using XIVault.Desktop.Services;

namespace XIVault.Desktop.ViewModels;

/// <summary>Settings save as soon as they change; they apply to the next backup.</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly IConfigStore _configStore;
    private readonly IXivLauncherLocator _locator;
    private readonly IFilePicker _picker;
    private readonly DesktopSession _session;
    private readonly PathDisplay _paths;
    // Saving is off until the stored settings are loaded, so constructor defaults never overwrite them.
    private bool _loading = true;

    public SettingsViewModel(IConfigStore configStore, IXivLauncherLocator locator, IFilePicker picker, DesktopSession session, PathDisplay paths)
    {
        _configStore = configStore;
        _locator = locator;
        _picker = picker;
        _session = session;
        _paths = paths;
        Compression = CompressionOptions[1];
    }

    public override AppPage Page => AppPage.Settings;

    public IReadOnlyList<Option<CompressionPreset>> CompressionOptions { get; } =
    [
        new(CompressionPreset.Fast, "Fast"),
        new(CompressionPreset.Balanced, "Balanced"),
        new(CompressionPreset.Maximum, "Maximum"),
    ];

    [ObservableProperty]
    public partial string Destination { get; private set; } = "";

    [ObservableProperty]
    public partial int Retention { get; private set; } = XivaultConfig.DefaultRetentionCount;

    [ObservableProperty]
    public partial bool IncludeUi { get; set; }

    [ObservableProperty]
    public partial Option<CompressionPreset> Compression { get; set; }

    [ObservableProperty]
    public partial string DetectedPath { get; private set; } = "";

    [ObservableProperty]
    public partial Tone DetectedTone { get; private set; }

    [ObservableProperty]
    public partial string DetectedLabel { get; private set; } = "";

    [ObservableProperty]
    public partial string? DetectedNote { get; private set; }

    [ObservableProperty]
    public partial bool HasOverride { get; private set; }

    [ObservableProperty]
    public partial bool IsOverrideOpen { get; private set; }

    [ObservableProperty]
    public partial string OverrideText { get; set; } = "";

    [ObservableProperty]
    public partial string? OverrideError { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    public string OverrideButtonLabel => IsOverrideOpen ? "Cancel" : "Override path";

    public string CompressionHint => Compression?.Value switch
    {
        CompressionPreset.Fast => "Largest files, quickest backups.",
        CompressionPreset.Maximum => "Smallest files, a few seconds slower.",
        _ => _session.Status?.LatestBackup is { } latest
            ? $"Default. About {Formatting.Bytes(latest.SizeBytes)} per backup."
            : "Default. A good balance of size and speed.",
    };

    partial void OnIsOverrideOpenChanged(bool value) => OnPropertyChanged(nameof(OverrideButtonLabel));

    partial void OnIncludeUiChanged(bool value) => Save(config => config with { IncludeDalamudUi = value });

    partial void OnCompressionChanged(Option<CompressionPreset> value)
    {
        OnPropertyChanged(nameof(CompressionHint));
        Save(config => config with { Compression = value.Value });
    }

    public override Task ActivateAsync()
    {
        Load();
        return Task.CompletedTask;
    }

    public void Load()
    {
        _loading = true;
        try
        {
            var config = _configStore.Load();
            Destination = config.BackupDestination!;
            Retention = config.RetentionCount;
            IncludeUi = config.IncludeDalamudUi;
            Compression = CompressionOptions.First(option => option.Value == config.Compression);
            HasOverride = config.XivLauncherPathOverride is not null;
            ErrorMessage = null;

            var located = _locator.Locate();
            if (located.Installation is { } installation)
            {
                DetectedPath = installation.RootPath;
                DetectedTone = Tone.Healthy;
                DetectedLabel = HasOverride ? "Custom" : "Detected";
                DetectedNote = installation.HasPortableConfiguration ? null : "Dalamud has not saved any settings here yet. You can still restore into it.";
            }
            else
            {
                DetectedPath = config.XivLauncherPathOverride ?? "Not found";
                DetectedTone = Tone.Critical;
                DetectedLabel = "Not found";
                DetectedNote = located.Message;
            }
        }
        catch (XivaultException ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        var folder = await _picker.PickFolderAsync("Choose where XIVault keeps backups", Destination);
        if (folder is not null)
        {
            Save(config => config with { BackupDestination = folder });
            Destination = folder;
            await _session.RefreshAsync(verifyLatest: false);
        }
    }

    [RelayCommand]
    private void RetentionDown() => SetRetention(Retention - 1);

    [RelayCommand]
    private void RetentionUp() => SetRetention(Retention + 1);

    [RelayCommand]
    private void ToggleOverride()
    {
        IsOverrideOpen = !IsOverrideOpen;
        OverrideError = null;
        OverrideText = IsOverrideOpen ? _configStore.Load().XivLauncherPathOverride ?? "" : "";
    }

    [RelayCommand]
    private async Task BrowseOverrideAsync()
    {
        var folder = await _picker.PickFolderAsync("Choose the XIVLauncher folder", OverrideText.Length > 0 ? OverrideText : null);
        if (folder is not null)
        {
            OverrideText = folder;
        }
    }

    [RelayCommand]
    private async Task UseOverrideAsync()
    {
        OverrideError = null;
        var path = OverrideText.Trim().Trim('"');
        if (path.Length == 0 || !Path.IsPathFullyQualified(path))
        {
            OverrideError = "Enter the full path of the XIVLauncher folder, such as D:\\Games\\XIVLauncher.";
            return;
        }

        // An existing folder alone isn't enough: it must hold XIVLauncher or Dalamud files.
        var located = _locator.Locate(path);
        if (!located.IsFound)
        {
            OverrideError = located.Message;
            return;
        }

        Save(config => config with { XivLauncherPathOverride = Path.GetFullPath(path) });
        IsOverrideOpen = false;
        Load();
        await _session.RefreshAsync(verifyLatest: false);
    }

    [RelayCommand]
    private async Task ResetOverrideAsync()
    {
        Save(config => config with { XivLauncherPathOverride = null });
        IsOverrideOpen = false;
        Load();
        await _session.RefreshAsync(verifyLatest: false);
    }

    private void SetRetention(int value)
    {
        value = Math.Clamp(value, XivaultConfig.MinRetentionCount, XivaultConfig.MaxRetentionCount);
        if (value != Retention)
        {
            Save(config => config with { RetentionCount = value });
            Retention = value;
        }
    }

    private void Save(Func<XivaultConfig, XivaultConfig> change)
    {
        if (_loading)
        {
            return;
        }

        try
        {
            _configStore.Save(change(_configStore.Load()));
            ErrorMessage = null;
        }
        catch (XivaultException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    public string FriendlyDestination => _paths.Friendly(Destination);
}

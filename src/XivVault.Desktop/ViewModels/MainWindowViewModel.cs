using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XivVault.Core;
using XivVault.Core.Platform;
using XivVault.Desktop.Controls;
using XivVault.Desktop.Services;

namespace XivVault.Desktop.ViewModels;

public sealed partial class NavItemViewModel(AppPage page, string label, string icon) : ObservableObject
{
    public AppPage Page { get; } = page;

    public string Label { get; } = label;

    public string Icon { get; } = icon;

    [ObservableProperty]
    public partial bool IsActive { get; set; }
}

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly Dictionary<AppPage, PageViewModel> _pages;
    private readonly DesktopSession _session;
    private readonly IShellService _shell;
    private readonly PathDisplay _paths;

    public MainWindowViewModel(
        IEnumerable<PageViewModel> pages,
        Navigator navigator,
        DesktopSession session,
        OverlayDialogService dialogs,
        UpdatesViewModel updates,
        IShellService shell,
        PathDisplay paths)
    {
        _pages = pages.ToDictionary(page => page.Page);
        _session = session;
        _shell = shell;
        _paths = paths;
        Dialogs = dialogs;
        Updates = updates;
        NavItems =
        [
            new(AppPage.Overview, "Overview", "LayoutDashboard"),
            new(AppPage.Backups, "Backups", "Archive"),
            new(AppPage.Restore, "Restore", "RotateCcw"),
            new(AppPage.Schedule, "Schedule", "CalendarClock"),
            new(AppPage.Diagnostics, "Diagnostics", "Stethoscope"),
            new(AppPage.Settings, "Settings", "Settings"),
        ];
        navigator.Navigated += (page, backup) => _ = ShowAsync(page, backup);
        _session.StatusChanged += (_, _) => UpdateConnection();
        CurrentPage = _pages[AppPage.Overview];
        NavItems[0].IsActive = true;
    }

    public ObservableCollection<NavItemViewModel> NavItems { get; }

    public OverlayDialogService Dialogs { get; }

    public UpdatesViewModel Updates { get; }

    [ObservableProperty]
    public partial PageViewModel CurrentPage { get; private set; }

    [ObservableProperty]
    public partial Tone ConnectionTone { get; private set; } = Tone.Unknown;

    [ObservableProperty]
    public partial string ConnectionLabel { get; private set; } = "Checking backup folder";

    public string VersionLabel { get; } = "XIV Vault " + XivVaultInfo.Version;

    public async Task InitializeAsync()
    {
        await ShowAsync(AppPage.Overview, null);
        await Updates.CheckOnStartupAsync();
    }

    [RelayCommand]
    private Task NavigateAsync(AppPage page) => ShowAsync(page, null);

    [RelayCommand]
    private Task OpenUpdatesAsync() => ShowAsync(AppPage.Settings, null);

    [RelayCommand]
    private void OpenGitHub() => _shell.OpenUrl(XivVaultInfo.RepositoryUrl);

    public async Task ShowAsync(AppPage page, string? backup)
    {
        foreach (var item in NavItems)
        {
            item.IsActive = item.Page == page;
        }

        var target = _pages[page];
        // Coming back to an unfinished wizard keeps its place; a chosen backup or a finished restore starts over.
        if (target is RestoreViewModel restore && (backup is not null || restore.IsStep4))
        {
            restore.Begin(backup);
        }

        CurrentPage = target;
        await target.ActivateAsync();
    }

    private void UpdateConnection()
    {
        if (_session.Status is not { } status)
        {
            return;
        }

        var friendly = _paths.Friendly(status.Destination);
        var service = friendly.StartsWith("OneDrive", StringComparison.Ordinal) ? "OneDrive"
            : friendly.StartsWith("Dropbox", StringComparison.Ordinal) ? "Dropbox"
            : "Backup folder";
        if (status.DestinationAvailable)
        {
            ConnectionTone = Tone.Healthy;
            ConnectionLabel = service == "Backup folder" ? "Backup folder available" : $"{service} connected";
        }
        else
        {
            var root = Path.GetPathRoot(status.Destination);
            var reachable = root is not null && Directory.Exists(root);
            ConnectionTone = reachable ? Tone.Unknown : Tone.Warning;
            ConnectionLabel = reachable ? "Backup folder not created yet" : "Backup folder unavailable";
        }
    }
}

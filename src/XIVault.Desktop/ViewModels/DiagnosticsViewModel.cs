using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XIVault.Core;
using XIVault.Core.Diagnostics;
using XIVault.Desktop.Controls;
using XIVault.Desktop.Services;

namespace XIVault.Desktop.ViewModels;

public sealed record DiagnosticRow(Tone Tone, string Icon, string Label, string Detail, bool HasDivider);

public sealed record DiagnosticGroupViewModel(string Title, string Icon, Tone Tone, string StatusLabel, IReadOnlyList<DiagnosticRow> Rows);

public sealed partial class DiagnosticsViewModel(
    IDiagnosticsService diagnostics,
    IClipboardService clipboard,
    INavigator navigator,
    TimeProvider clock) : PageViewModel
{
    private DiagnosticReport? _report;
    private DiagnosticArea? _bannerArea;

    public override AppPage Page => AppPage.Diagnostics;

    public ObservableCollection<DiagnosticGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    public partial string Subtitle { get; private set; } = "Running checks…";

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    public partial bool Copied { get; private set; }

    [ObservableProperty]
    public partial Tone BannerTone { get; private set; }

    [ObservableProperty]
    public partial string? BannerTitle { get; private set; }

    [ObservableProperty]
    public partial string? BannerDescription { get; private set; }

    [ObservableProperty]
    public partial string? BannerAction { get; private set; }

    public bool HasBanner => BannerTitle is not null;

    public string CopyLabel => Copied ? "Copied" : "Copy Diagnostic Report";

    public string CopyIcon => Copied ? "Check" : "Copy";

    partial void OnCopiedChanged(bool value)
    {
        OnPropertyChanged(nameof(CopyLabel));
        OnPropertyChanged(nameof(CopyIcon));
    }

    partial void OnBannerTitleChanged(string? value) => OnPropertyChanged(nameof(HasBanner));

    public override Task ActivateAsync() => RunAsync();

    [RelayCommand]
    private async Task RunAsync()
    {
        IsRunning = true;
        try
        {
            var report = await diagnostics.RunAsync();
            _report = report;
            Subtitle = $"{report.Summary} · checked {Formatting.Time(clock.GetLocalNow().DateTime)}";
            Groups.Clear();
            foreach (var group in report.Groups)
            {
                Groups.Add(new DiagnosticGroupViewModel(
                    group.Title,
                    group.Area switch
                    {
                        DiagnosticArea.XivLauncher => "Monitor",
                        DiagnosticArea.Dalamud => "Boxes",
                        DiagnosticArea.BackupDestination => "Cloud",
                        _ => "CalendarClock",
                    },
                    ToneOf(group.Status),
                    DiagnosticReport.Label(group.Status),
                    group.Checks.Select((check, index) => new DiagnosticRow(
                        ToneOf(check.Status),
                        check.Status == DiagnosticStatus.Healthy ? "Check" : check.Status == DiagnosticStatus.Warning ? "TriangleAlert" : "X",
                        check.Label,
                        check.Detail,
                        index > 0)).ToList()));
            }

            BuildBanner(report);
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private async Task CopyReportAsync()
    {
        if (_report is null)
        {
            return;
        }

        await clipboard.SetTextAsync(_report.ToText());
        Copied = true;
        await Task.Delay(TimeSpan.FromSeconds(2));
        Copied = false;
    }

    [RelayCommand]
    private void BannerGo() => navigator.Navigate(_bannerArea switch
    {
        DiagnosticArea.Scheduling => AppPage.Schedule,
        _ => AppPage.Settings,
    });

    private void BuildBanner(DiagnosticReport report)
    {
        var worst = report.Groups
            .SelectMany(group => group.Checks.Select(check => (group.Area, Check: check)))
            .Where(item => item.Check.Status != DiagnosticStatus.Healthy)
            .OrderByDescending(item => item.Check.Status)
            .Select(item => ((DiagnosticArea, DiagnosticCheck)?)item)
            .FirstOrDefault();
        if (worst is not var (area, check))
        {
            BannerTitle = null;
            BannerDescription = null;
            BannerAction = null;
            _bannerArea = null;
            return;
        }

        _bannerArea = area;
        BannerTone = check.Status == DiagnosticStatus.Error ? Tone.Critical : Tone.Warning;
        if (check.Detail.Contains("same drive as XIVLauncher", StringComparison.Ordinal))
        {
            BannerTitle = "Backup destination is on the same physical drive as your XIVLauncher data.";
            BannerDescription = "Consider using OneDrive, Dropbox, NAS, or another disk for stronger protection. A drive failure would take both the configuration and its backups.";
        }
        else
        {
            BannerTitle = check.Label;
            BannerDescription = check.Detail;
        }

        BannerAction = area switch
        {
            DiagnosticArea.BackupDestination => "Change destination",
            DiagnosticArea.Scheduling => "Open Schedule",
            _ => "Open Settings",
        };
    }

    private static Tone ToneOf(DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Healthy => Tone.Healthy,
        DiagnosticStatus.Warning => Tone.Warning,
        _ => Tone.Critical,
    };
}

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XIVault.Core;
using XIVault.Core.Configuration;
using XIVault.Core.Scheduling;
using XIVault.Core.State;
using XIVault.Desktop.Controls;
using XIVault.Desktop.Services;

namespace XIVault.Desktop.ViewModels;

public sealed partial class WeekdayViewModel(DayOfWeek day) : ObservableObject
{
    public DayOfWeek Day { get; } = day;

    public string Label { get; } = day.ToString()[..3];

    [ObservableProperty]
    public partial bool IsOn { get; set; }
}

/// <summary>Automatic backups. Every change is applied to the Windows task straight away.</summary>
public sealed partial class ScheduleViewModel : PageViewModel
{
    private static readonly (string Value, string Label)[] DefaultTimes =
        [("06:00", "6:00 AM"), ("09:00", "9:00 AM"), ("12:00", "12:00 PM"), ("18:00", "6:00 PM"), ("21:00", "9:00 PM"), ("23:30", "11:30 PM")];

    private readonly ScheduleService _schedules;
    private readonly IConfigStore _configStore;
    private readonly IFilePicker _picker;
    private readonly ISchedulerTarget _target;
    private readonly DesktopSession _session;
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    // Nothing is applied to Windows until the real schedule has been read; otherwise the defaults
    // set while constructing this view model would overwrite (or remove) the user's task.
    private bool _loading = true;

    public ScheduleViewModel(
        ScheduleService schedules,
        IConfigStore configStore,
        IFilePicker picker,
        ISchedulerTarget target,
        DesktopSession session)
    {
        _schedules = schedules;
        _configStore = configStore;
        _picker = picker;
        _target = target;
        _session = session;
        Frequency = Frequencies[1];
        SelectedTime = TimeOptions[2];
        foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday })
        {
            Weekdays.Add(new WeekdayViewModel(day));
        }
    }

    public override AppPage Page => AppPage.Schedule;

    public IReadOnlyList<Option<ScheduleFrequency>> Frequencies { get; } =
    [
        new(ScheduleFrequency.Daily, "Daily"),
        new(ScheduleFrequency.Weekly, "Weekly"),
        new(ScheduleFrequency.AtLogon, "At Windows login"),
    ];

    public ObservableCollection<Option<string>> TimeOptions { get; } = new(DefaultTimes.Select(time => new Option<string>(time.Value, time.Label)));

    public ObservableCollection<WeekdayViewModel> Weekdays { get; } = [];

    [ObservableProperty]
    public partial bool AutoEnabled { get; set; }

    [ObservableProperty]
    public partial Option<ScheduleFrequency> Frequency { get; set; }

    [ObservableProperty]
    public partial Option<string> SelectedTime { get; set; }

    [ObservableProperty]
    public partial int Retention { get; private set; } = XivaultConfig.DefaultRetentionCount;

    [ObservableProperty]
    public partial string Destination { get; private set; } = "";

    [ObservableProperty]
    public partial string NextLabel { get; private set; } = "Paused";

    [ObservableProperty]
    public partial string LastRunLabel { get; private set; } = "No scheduled run yet";

    [ObservableProperty]
    public partial Tone LastRunTone { get; private set; } = Tone.Unknown;

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    public partial bool IsApplying { get; private set; }

    public bool IsWeekly => Frequency?.Value == ScheduleFrequency.Weekly;

    public bool HasTime => Frequency?.Value != ScheduleFrequency.AtLogon;

    public string AutoDescription => AutoEnabled
        ? "On · runs as a Windows scheduled task even when XIVault is closed"
        : "Off · backups only happen when you press Back Up Now";

    public string RetentionHint
    {
        get
        {
            var latest = _session.Status?.LatestBackup?.SizeBytes;
            var estimate = latest is { } size ? $"about {Formatting.Bytes(size * Retention)}" : "space depends on your plugins";
            return $"backups · {estimate}. Older backups are removed after each new one is verified.";
        }
    }

    partial void OnAutoEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(AutoDescription));
        _ = ApplyAsync();
    }

    partial void OnFrequencyChanged(Option<ScheduleFrequency> value)
    {
        OnPropertyChanged(nameof(IsWeekly));
        OnPropertyChanged(nameof(HasTime));
        _ = ApplyAsync();
    }

    partial void OnSelectedTimeChanged(Option<string> value) => _ = ApplyAsync();

    partial void OnRetentionChanged(int value) => OnPropertyChanged(nameof(RetentionHint));

    public override async Task ActivateAsync()
    {
        _loading = true;
        var config = _configStore.Load();
        var status = await _schedules.GetStatusAsync();
        try
        {
            // The Windows task is the truth when it exists; otherwise the saved preferences are shown.
            var settings = status.Installed && status.InstalledSettings is { } installed ? installed with { Enabled = true } : config.Schedule with { Enabled = false };
            AutoEnabled = settings.Enabled;
            Frequency = Frequencies.First(option => option.Value == settings.Frequency);
            var time = TimeOptions.FirstOrDefault(option => option.Value == settings.Time);
            if (time is null)
            {
                time = new Option<string>(settings.Time, Formatting.Time(DateTime.Today + settings.TimeOfDay.ToTimeSpan()));
                TimeOptions.Add(time);
            }

            SelectedTime = time;
            var days = settings.Days.Count > 0 ? settings.Days : [DayOfWeek.Sunday];
            foreach (var weekday in Weekdays)
            {
                weekday.IsOn = days.Contains(weekday.Day);
            }

            Retention = config.RetentionCount;
            Destination = config.BackupDestination!;
            UpdateRunLabels(status);
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(RetentionHint));
    }

    [RelayCommand]
    private async Task ToggleDayAsync(WeekdayViewModel weekday)
    {
        // At least one day stays selected, as in the mockup.
        if (weekday.IsOn && Weekdays.Count(day => day.IsOn) == 1)
        {
            return;
        }

        weekday.IsOn = !weekday.IsOn;
        await ApplyAsync();
    }

    [RelayCommand]
    private void RetentionDown() => SaveRetention(Retention - 1);

    [RelayCommand]
    private void RetentionUp() => SaveRetention(Retention + 1);

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        var folder = await _picker.PickFolderAsync("Choose where XIVault keeps backups", Destination);
        if (folder is null)
        {
            return;
        }

        _configStore.Save(_configStore.Load() with { BackupDestination = folder });
        Destination = folder;
        await _session.RefreshAsync(verifyLatest: false);
    }

    private void SaveRetention(int value)
    {
        value = Math.Clamp(value, XivaultConfig.MinRetentionCount, XivaultConfig.MaxRetentionCount);
        if (value == Retention)
        {
            return;
        }

        _configStore.Save(_configStore.Load() with { RetentionCount = value });
        Retention = value;
    }

    public async Task ApplyAsync()
    {
        if (_loading || Frequency is null || SelectedTime is null)
        {
            return;
        }

        await _applyGate.WaitAsync();
        IsApplying = true;
        ErrorMessage = null;
        try
        {
            var settings = new ScheduleSettings
            {
                Enabled = AutoEnabled,
                Frequency = Frequency.Value,
                Days = Weekdays.Where(day => day.IsOn).Select(day => day.Day).ToList(),
                Time = SelectedTime.Value,
            };
            var status = await _schedules.ApplyAsync(settings, _target.Command);
            UpdateRunLabels(status);
        }
        catch (XivaultException ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsApplying = false;
            _applyGate.Release();
        }

        await _session.RefreshAsync(verifyLatest: false);
    }

    private void UpdateRunLabels(ScheduleStatus status)
    {
        var now = _session.Clock.GetLocalNow().DateTime;
        NextLabel = !status.Installed
            ? "Paused"
            : status.NextRunLocal is { } next
                ? NextRunText(next, now)
                : "At next Windows login";
        (LastRunTone, LastRunLabel) = status.LastRun switch
        {
            { Result: ScheduledRunResult.Success } run => (Tone.Healthy, "Successful · " + Short(run.AtUtc, now)),
            { Result: ScheduledRunResult.Skipped } run => (Tone.Warning, "Skipped · " + Short(run.AtUtc, now)),
            { Result: ScheduledRunResult.Failed } run => (Tone.Critical, "Failed · " + Short(run.AtUtc, now)),
            _ => (Tone.Unknown, "No scheduled run yet"),
        };
    }

    private static string NextRunText(DateTime next, DateTime now)
    {
        var days = (next.Date - now.Date).Days;
        var day = days switch
        {
            0 => "Today",
            1 => "Tomorrow",
            < 7 => next.ToString("dddd", CultureInfo.InvariantCulture),
            _ => next.ToString("MMM d", CultureInfo.InvariantCulture),
        };
        return $"{day} · {Formatting.Time(next)}";
    }

    private static string Short(DateTime utc, DateTime now)
    {
        var local = utc.ToLocalTime();
        return $"{(local.Date == now.Date ? "Today" : local.ToString("MMM d", CultureInfo.InvariantCulture))}, {Formatting.Time(local)}";
    }
}

using System.ComponentModel;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using XivVault.Cli.Infrastructure;
using XivVault.Core;
using XivVault.Core.Configuration;
using XivVault.Core.Scheduling;

namespace XivVault.Cli.Commands;

internal class ScheduleTimeSettings : GlobalSettings
{
    [CommandOption("-t|--time <HH:MM>")]
    [Description("Time of day, 24-hour, such as 18:30. Defaults to the saved time (12:00).")]
    public string? Time { get; init; }

    public override ValidationResult Validate() =>
        Time is null || TimeOnly.TryParseExact(Time, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? ValidationResult.Success()
            : ValidationResult.Error("--time must be a 24-hour time such as 18:30.");

    public string ResolveTime(ScheduleSettings saved) =>
        Time is null ? saved.Time : TimeOnly.ParseExact(Time, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture).ToString("HH:mm", CultureInfo.InvariantCulture);
}

internal sealed class WeeklySettings : ScheduleTimeSettings
{
    [CommandOption("-d|--day <DAY>")]
    [Description("Day of the week, such as Sunday. Repeat it or separate with commas for several days.")]
    public string[] Days { get; init; } = [];

    public override ValidationResult Validate()
    {
        var time = base.Validate();
        if (!time.Successful)
        {
            return time;
        }

        return ParseDays(Days) is null
            ? ValidationResult.Error("--day must be a day of the week, such as Sunday or Sun.")
            : ValidationResult.Success();
    }

    public static List<DayOfWeek>? ParseDays(IEnumerable<string> values)
    {
        var days = new List<DayOfWeek>();
        foreach (var value in values.SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            var match = Enum.GetValues<DayOfWeek>().FirstOrDefault(
                day => day.ToString().StartsWith(value, StringComparison.OrdinalIgnoreCase) && value.Length >= 2,
                (DayOfWeek)(-1));
            if ((int)match < 0)
            {
                return null;
            }

            if (!days.Contains(match))
            {
                days.Add(match);
            }
        }

        return days;
    }
}

/// <summary>Shared by daily, weekly and logon: saves the preferences and installs the task.</summary>
internal abstract class ScheduleApplyCommand<TSettings>(CliOutput output, ScheduleService schedules, IConfigStore configStore)
    : XivVaultCommand<TSettings>(output)
    where TSettings : GlobalSettings
{
    /// <summary>What the Windows task runs. The CLI schedules itself.</summary>
    internal static Func<ScheduledCommand> CommandFactory { get; set; } = () => ScheduledCommand.ForCurrentProcess("backup --scheduled --quiet");

    protected abstract ScheduleSettings Build(TSettings settings, ScheduleSettings saved);

    protected override async Task<int> RunAsync(TSettings settings, CancellationToken cancellationToken)
    {
        var desired = Build(settings, configStore.Load().Schedule) with { Enabled = true };
        var status = await schedules.ApplyAsync(desired, CommandFactory(), cancellationToken);
        Output.Success($"Automatic backups on: {Formatting.Schedule(desired)}");
        Output.Detail(status.NextRunLocal is { } next
            ? $"Next backup {Formatting.FullDate(next)}. Runs as the Windows task \"{WindowsTaskScheduler.TaskName}\"; XIV Vault does not need to stay open."
            : $"Runs as the Windows task \"{WindowsTaskScheduler.TaskName}\" at your next sign-in.");
        return 0;
    }
}

internal sealed class ScheduleDailyCommand(CliOutput output, ScheduleService schedules, IConfigStore configStore)
    : ScheduleApplyCommand<ScheduleTimeSettings>(output, schedules, configStore)
{
    protected override ScheduleSettings Build(ScheduleTimeSettings settings, ScheduleSettings saved) =>
        saved with { Frequency = ScheduleFrequency.Daily, Time = settings.ResolveTime(saved) };
}

internal sealed class ScheduleWeeklyCommand(CliOutput output, ScheduleService schedules, IConfigStore configStore)
    : ScheduleApplyCommand<WeeklySettings>(output, schedules, configStore)
{
    protected override ScheduleSettings Build(WeeklySettings settings, ScheduleSettings saved)
    {
        var days = WeeklySettings.ParseDays(settings.Days) ?? [];
        return saved with
        {
            Frequency = ScheduleFrequency.Weekly,
            Days = days.Count > 0 ? days : saved.Days.Count > 0 ? saved.Days : [DayOfWeek.Sunday],
            Time = settings.ResolveTime(saved),
        };
    }
}

internal sealed class ScheduleLogonCommand(CliOutput output, ScheduleService schedules, IConfigStore configStore)
    : ScheduleApplyCommand<GlobalSettings>(output, schedules, configStore)
{
    protected override ScheduleSettings Build(GlobalSettings settings, ScheduleSettings saved) =>
        saved with { Frequency = ScheduleFrequency.AtLogon };
}

internal sealed class ScheduleRemoveCommand(CliOutput output, ScheduleService schedules) : XivVaultCommand<GlobalSettings>(output)
{
    protected override async Task<int> RunAsync(GlobalSettings settings, CancellationToken cancellationToken)
    {
        await schedules.DisableAsync(cancellationToken);
        Output.Success("Automatic backups off. The Windows task was removed; your schedule preferences are kept.");
        return 0;
    }
}

internal sealed class ScheduleStatusCommand(CliOutput output, ScheduleService schedules, TimeProvider clock)
    : XivVaultCommand<ScheduleStatusCommand.Settings>(output)
{
    internal sealed class Settings : GlobalSettings
    {
        [CommandOption("--json")]
        [Description("Print the schedule as JSON.")]
        public bool Json { get; init; }
    }

    protected override async Task<int> RunAsync(Settings settings, CancellationToken cancellationToken)
    {
        var status = await schedules.GetStatusAsync(cancellationToken);
        if (settings.Json)
        {
            Output.Json(JsonModels.From(status));
            return 0;
        }

        var now = clock.GetLocalNow().DateTime;
        if (!status.Installed || status.InstalledSettings is null)
        {
            Output.Line("Automatic backups are off.");
            Output.Detail("Turn them on with: xiv-vault schedule weekly --day Sunday");
        }
        else
        {
            Output.Markup($"[{CliOutput.Healthy}]●[/] {Markup.Escape(Formatting.Schedule(status.InstalledSettings))}");
            Output.Detail(status.NextRunLocal is { } next ? $"Next backup: {Formatting.DayAndTime(next, now)}" : "Next backup: at next Windows login");
            if (status.CommandMissing)
            {
                Output.Warning("The task points to a program that no longer exists. Run the schedule command again from this copy of XIV Vault.");
            }
        }

        if (status.LastRun is { } last)
        {
            Output.Detail($"Last scheduled run: {Formatting.DayAndTime(last.AtUtc.ToLocalTime(), now)} · {last.Result.ToString().ToLowerInvariant()} · {last.Message}");
        }

        return 0;
    }
}

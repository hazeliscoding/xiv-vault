using XivVault.Core.Configuration;
using XivVault.Core.State;

namespace XivVault.Core.Scheduling;

/// <summary>The program and arguments the scheduled task runs.</summary>
public sealed record ScheduledCommand(string Executable, string Arguments)
{
    /// <summary>
    /// The command that re-runs the current program with <paramref name="arguments"/>. Under
    /// <c>dotnet xiv-vault.dll</c> the process is dotnet.exe, so the DLL is passed through.
    /// </summary>
    public static ScheduledCommand ForCurrentProcess(string arguments)
    {
        var process = Environment.ProcessPath ?? throw new InvalidOperationException("The current program path is unknown.");
        if (Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && System.Reflection.Assembly.GetEntryAssembly()?.Location is { Length: > 0 } entry)
        {
            return new ScheduledCommand(process, $"\"{entry}\" {arguments}");
        }

        return new ScheduledCommand(process, arguments);
    }
}

public sealed record ScheduleStatus(
    bool Installed,
    ScheduleSettings? InstalledSettings,
    ScheduledCommand? Command,
    DateTime? NextRunLocal,
    ScheduledRunRecord? LastRun,
    string? Problem)
{
    public bool CommandMissing => Command is not null && !File.Exists(Command.Executable);
}

public static class NextRun
{
    /// <summary>The next local time the schedule fires, or null for "at next Windows login".</summary>
    public static DateTime? After(ScheduleSettings settings, DateTime nowLocal)
    {
        var time = settings.TimeOfDay.ToTimeSpan();
        switch (settings.Frequency)
        {
            case ScheduleFrequency.AtLogon:
                return null;
            case ScheduleFrequency.Daily:
                {
                    var today = nowLocal.Date + time;
                    return today > nowLocal ? today : today.AddDays(1);
                }

            default:
                {
                    if (settings.Days.Count == 0)
                    {
                        return null;
                    }

                    for (var offset = 0; offset <= 7; offset++)
                    {
                        var candidate = nowLocal.Date.AddDays(offset) + time;
                        if (candidate > nowLocal && settings.Days.Contains(candidate.DayOfWeek))
                        {
                            return candidate;
                        }
                    }

                    return null;
                }
        }
    }
}

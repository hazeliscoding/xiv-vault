using System.Globalization;
using System.Xml.Linq;
using XIVault.Core.Configuration;

namespace XIVault.Core.Scheduling;

/// <summary>Builds and reads the Task Scheduler XML for the backup task.</summary>
public static class TaskXml
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static readonly DayOfWeek[] WeekOrder =
        [DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday];

    public static string Build(ScheduleSettings settings, ScheduledCommand command, string userAccount, DateTime nowLocal)
    {
        var start = (nowLocal.Date + settings.TimeOfDay.ToTimeSpan()).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        XElement trigger = settings.Frequency switch
        {
            ScheduleFrequency.AtLogon => new XElement(
                Ns + "LogonTrigger",
                new XElement(Ns + "Enabled", "true"),
                new XElement(Ns + "UserId", userAccount),

                // Give OneDrive and the network a moment after sign-in.
                new XElement(Ns + "Delay", "PT2M")),
            ScheduleFrequency.Daily => new XElement(
                Ns + "CalendarTrigger",
                new XElement(Ns + "StartBoundary", start),
                new XElement(Ns + "Enabled", "true"),
                new XElement(Ns + "ScheduleByDay", new XElement(Ns + "DaysInterval", "1"))),
            _ => new XElement(
                Ns + "CalendarTrigger",
                new XElement(Ns + "StartBoundary", start),
                new XElement(Ns + "Enabled", "true"),
                new XElement(
                    Ns + "ScheduleByWeek",
                    new XElement(Ns + "DaysOfWeek", WeekOrder.Where(settings.Days.Contains).Select(day => new XElement(Ns + day.ToString()))),
                    new XElement(Ns + "WeeksInterval", "1"))),
        };

        var task = new XElement(
            Ns + "Task",
            new XAttribute("version", "1.2"),
            new XElement(
                Ns + "RegistrationInfo",
                new XElement(Ns + "Description", "Backs up the portable XIVLauncher / Dalamud configuration with XIV Vault."),
                new XElement(Ns + "URI", @"\" + WindowsTaskScheduler.TaskName)),
            new XElement(Ns + "Triggers", trigger),
            new XElement(
                Ns + "Principals",
                new XElement(
                    Ns + "Principal",
                    new XAttribute("id", "Author"),
                    new XElement(Ns + "UserId", userAccount),
                    new XElement(Ns + "LogonType", "InteractiveToken"),
                    new XElement(Ns + "RunLevel", "LeastPrivilege"))),
            new XElement(
                Ns + "Settings",
                new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                new XElement(Ns + "StopIfGoingOnBatteries", "false"),

                // A PC that was off at the scheduled time backs up when it next starts.
                new XElement(Ns + "StartWhenAvailable", "true"),
                new XElement(Ns + "RunOnlyIfNetworkAvailable", "false"),
                new XElement(Ns + "IdleSettings", new XElement(Ns + "StopOnIdleEnd", "false"), new XElement(Ns + "RestartOnIdle", "false")),
                new XElement(Ns + "AllowStartOnDemand", "true"),
                new XElement(Ns + "Enabled", "true"),
                new XElement(Ns + "Hidden", "false"),

                // Long enough to wait out a play session; the run gives up after 6 hours itself.
                new XElement(Ns + "ExecutionTimeLimit", "PT8H"),
                new XElement(Ns + "Priority", "7")),
            new XElement(
                Ns + "Actions",
                new XAttribute("Context", "Author"),
                new XElement(
                    Ns + "Exec",
                    new XElement(Ns + "Command", command.Executable),
                    new XElement(Ns + "Arguments", command.Arguments))));

        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" + task;
    }

    /// <summary>Reads back the schedule and command. Returns nulls for parts it doesn't recognize.</summary>
    public static (ScheduleSettings? Settings, ScheduledCommand? Command) Parse(string xml)
    {
        var start = xml.IndexOf('<');
        if (start < 0)
        {
            return (null, null);
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(xml[start..]);
        }
        catch (System.Xml.XmlException)
        {
            return (null, null);
        }

        var root = document.Root;
        if (root is null)
        {
            return (null, null);
        }

        var ns = root.Name.Namespace;
        var exec = root.Element(ns + "Actions")?.Element(ns + "Exec");
        var command = exec?.Element(ns + "Command")?.Value is { Length: > 0 } executable
            ? new ScheduledCommand(executable.Trim('"'), exec.Element(ns + "Arguments")?.Value ?? "")
            : null;

        var enabled = !string.Equals(root.Element(ns + "Settings")?.Element(ns + "Enabled")?.Value, "false", StringComparison.OrdinalIgnoreCase);
        var triggers = root.Element(ns + "Triggers");
        if (triggers is null)
        {
            return (null, command);
        }

        if (triggers.Element(ns + "LogonTrigger") is not null)
        {
            return (new ScheduleSettings { Enabled = enabled, Frequency = ScheduleFrequency.AtLogon }, command);
        }

        var calendar = triggers.Element(ns + "CalendarTrigger");
        if (calendar is null)
        {
            return (null, command);
        }

        var time = "12:00";
        if (DateTime.TryParse(calendar.Element(ns + "StartBoundary")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var boundary))
        {
            time = boundary.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        if (calendar.Element(ns + "ScheduleByWeek") is { } weekly)
        {
            var days = weekly.Element(ns + "DaysOfWeek")?.Elements()
                .Select(element => Enum.TryParse<DayOfWeek>(element.Name.LocalName, out var day) ? (DayOfWeek?)day : null)
                .OfType<DayOfWeek>()
                .ToList() ?? [];
            return (new ScheduleSettings { Enabled = enabled, Frequency = ScheduleFrequency.Weekly, Days = days, Time = time }, command);
        }

        return (new ScheduleSettings { Enabled = enabled, Frequency = ScheduleFrequency.Daily, Time = time }, command);
    }
}

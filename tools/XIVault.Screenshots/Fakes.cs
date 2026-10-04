using XIVault.Core.Configuration;
using XIVault.Core.Platform;
using XIVault.Core.Scheduling;
using XIVault.Desktop.Services;

namespace XIVault.Screenshots;

internal sealed class FakeEnvironment : IAppEnvironment
{
    public FakeEnvironment(string root)
    {
        // The root stands in for the user profile, so screenshots show short, neutral paths.
        UserProfile = root;
        RoamingAppData = Path.Combine(UserProfile, "AppData", "Roaming");
        LocalAppData = Path.Combine(UserProfile, "AppData", "Local");
        Documents = Path.Combine(UserProfile, "Documents");
        OneDrive = Path.Combine(UserProfile, "OneDrive");
        TempPath = Path.Combine(root, "Temp");
        DataDirectory = Path.Combine(LocalAppData, "XIVault");
        foreach (var folder in new[] { RoamingAppData, LocalAppData, Documents, OneDrive, TempPath })
        {
            Directory.CreateDirectory(folder);
        }
    }

    public string RoamingAppData { get; }

    public string LocalAppData { get; }

    public string UserProfile { get; }

    public string Documents { get; }

    public string? OneDrive { get; }

    public string TempPath { get; }

    public string UserAccount => @"MOCHI\roze";

    public string DataDirectory { get; }
}

/// <summary>A settable clock, so the history can be created "in the past" and shown from a fixed "now".</summary>
internal sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
}

internal sealed class NoProcesses : IProcessInspector
{
    public IReadOnlyList<string> FindRunning(IEnumerable<string> processNames) => [];
}

/// <summary>Pretends Task Scheduler holds a weekly task, so nothing real is ever registered.</summary>
internal sealed class FakeScheduler : ICommandRunner
{
    private string? _xml = TaskXml.Build(
        new ScheduleSettings { Enabled = true, Frequency = ScheduleFrequency.Weekly, Days = [DayOfWeek.Sunday], Time = "12:00" },
        new ScheduledCommand(Environment.ProcessPath!, "--scheduled-backup"),
        @"MOCHI\roze",
        new DateTime(2026, 9, 1));

    public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        switch (arguments[0])
        {
            case "/Create":
                _xml = File.ReadAllText(arguments[4]);
                return Task.FromResult(new CommandResult(0, "", ""));
            case "/Delete":
                _xml = null;
                return Task.FromResult(new CommandResult(0, "", ""));
            default:
                return Task.FromResult(_xml is null ? new CommandResult(1, "", "not found") : new CommandResult(0, _xml, ""));
        }
    }
}

internal sealed class StillMotion : IMotionSettings
{
    public bool ReduceMotion => true;
}

internal sealed class NoShell : IShellService
{
    public void RevealInExplorer(string path)
    {
    }

    public bool Launch(string executable) => true;

    public void OpenUrl(string url)
    {
    }
}

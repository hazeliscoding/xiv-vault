using XivVault.Core.Platform;

namespace XivVault.Tests.Support;

public sealed class FakeProcessInspector : IProcessInspector
{
    public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> FindRunning(IEnumerable<string> processNames) =>
        processNames.Where(Running.Contains).ToList();
}

/// <summary>A clock tests can move. Local time is UTC so file names are predictable, unless a test picks a zone.</summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow() => Now;

    public override TimeZoneInfo LocalTimeZone => Zone;

    public void Advance(TimeSpan by) => Now += by;
}

public sealed class FakeCommandRunner : ICommandRunner
{
    public List<(string File, IReadOnlyList<string> Arguments)> Calls { get; } = [];

    /// <summary>By default there is no scheduled task: queries fail and everything else succeeds.</summary>
    public Func<string, IReadOnlyList<string>, CommandResult> Handler { get; set; } = (_, args) =>
        args.Count > 0 && args[0] == "/Query" ? new CommandResult(1, "", "ERROR: not found") : new CommandResult(0, "", "");

    public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        Calls.Add((fileName, arguments.ToList()));
        return Task.FromResult(Handler(fileName, arguments));
    }
}

/// <summary>
/// Files tests mark as online only. Nothing downloads them, so a test can fill one with junk:
/// any code that reads it finds no valid archive.
/// </summary>
public sealed class FakeFileAvailability : IFileAvailability
{
    public HashSet<string> OnlineOnly { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsOnlineOnly(string path) => OnlineOnly.Contains(Path.GetFullPath(path));
}

using XIVault.Core.Platform;

namespace XIVault.Tests.Support;

public sealed class FakeProcessInspector : IProcessInspector
{
    public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> FindRunning(IEnumerable<string> processNames) =>
        processNames.Where(Running.Contains).ToList();
}

/// <summary>A clock tests can move. Local time is UTC so file names are predictable.</summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

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

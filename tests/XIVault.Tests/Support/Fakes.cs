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

    public Func<string, IReadOnlyList<string>, CommandResult> Handler { get; set; } = (_, _) => new CommandResult(0, "", "");

    public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        Calls.Add((fileName, arguments.ToList()));
        return Task.FromResult(Handler(fileName, arguments));
    }
}

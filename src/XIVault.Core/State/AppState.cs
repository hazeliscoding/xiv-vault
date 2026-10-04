using System.Text.Json;
using System.Text.Json.Serialization;
using XIVault.Core.Platform;
using XIVault.Core.Serialization;

namespace XIVault.Core.State;

/// <summary>What XIV Vault remembers between runs. Never holds configuration contents.</summary>
public sealed record AppState
{
    public ScheduledRunRecord? LastScheduledRun { get; init; }

    /// <summary>Verification results keyed by archive path, invalidated when size or modified time change.</summary>
    public Dictionary<string, VerificationRecord> Verifications { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record ScheduledRunRecord(DateTime AtUtc, ScheduledRunResult Result, string Message, string? BackupFile);

public enum ScheduledRunResult
{
    [JsonStringEnumMemberName("success")]
    Success,

    [JsonStringEnumMemberName("skipped")]
    Skipped,

    [JsonStringEnumMemberName("failed")]
    Failed,
}

public sealed record VerificationRecord(
    long Size,
    DateTime LastWriteUtc,
    bool Valid,
    string? ArchiveSha256,
    DateTime CheckedAtUtc,
    string? Problem);

public interface IStateStore
{
    AppState Load();

    void Update(Func<AppState, AppState> change);
}

public sealed class StateStore(IAppEnvironment environment) : IStateStore
{
    private static readonly Lock Gate = new();

    public string StatePath => Path.Combine(environment.DataDirectory, "state.json");

    public AppState Load()
    {
        lock (Gate)
        {
            return LoadUnlocked();
        }
    }

    public void Update(Func<AppState, AppState> change)
    {
        lock (Gate)
        {
            var next = change(LoadUnlocked());
            AtomicFile.WriteAllText(StatePath, JsonSerializer.Serialize(next, XivaultJsonContext.Default.AppState));
        }
    }

    private AppState LoadUnlocked()
    {
        if (!File.Exists(StatePath))
        {
            return new AppState();
        }

        try
        {
            var state = JsonSerializer.Deserialize(AtomicFile.ReadAllText(StatePath), XivaultJsonContext.Default.AppState) ?? new AppState();
            return state with { Verifications = new(state.Verifications, StringComparer.OrdinalIgnoreCase) };
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // State is a cache plus the last scheduled result; losing it is harmless.
            return new AppState();
        }
    }
}

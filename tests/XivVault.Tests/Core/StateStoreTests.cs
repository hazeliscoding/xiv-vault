using XivVault.Core.State;
using XivVault.Tests.Support;

namespace XivVault.Tests.Core;

public class StateStoreTests
{
    [Fact]
    public void A_state_file_without_verifications_loads_as_empty()
    {
        using var env = new TestEnvironment();
        var store = new StateStore(env);
        Directory.CreateDirectory(env.DataDirectory);
        File.WriteAllText(store.StatePath, "{}");

        var state = store.Load();

        Assert.Empty(state.Verifications);
        Assert.Null(state.LastScheduledRun);
    }

    [Fact]
    public void Verification_paths_match_regardless_of_case_after_loading()
    {
        using var env = new TestEnvironment();
        var store = new StateStore(env);
        var record = new VerificationRecord(1, DateTime.UtcNow, true, null, DateTime.UtcNow, null);
        store.Update(state => state with { Verifications = new(StringComparer.OrdinalIgnoreCase) { [@"D:\Backups\a.zip"] = record } });

        Assert.True(store.Load().Verifications.ContainsKey(@"d:\backups\A.ZIP"));
    }
}

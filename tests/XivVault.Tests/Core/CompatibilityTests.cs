using XivVault.Core.Backup;
using XivVault.Core.Restore;
using XivVault.Tests.Support;

namespace XivVault.Tests.Core;

/// <summary>
/// Archives made by the published 0.1.0 CLI from a fake XIVLauncher folder: a manual backup with
/// the UI layout, then the safety snapshot from restoring it after Artisan changed. They stand for
/// backups people already have, so never regenerate or edit them; every later version must
/// list, verify and restore them as they are.
/// </summary>
public class CompatibilityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "0.1.0");

    public static TheoryData<string, BackupKind, string> Archives => new()
    {
        { "xiv-vault-2026-10-11-191924.zip", BackupKind.Manual, """{ "Version": 1, "Plugin": "Artisan" }""" },
        { "pre-restore-2026-10-11-191926.zip", BackupKind.PreRestore, """{ "Version": 2, "Plugin": "Artisan" }""" },
    };

    [Theory]
    [MemberData(nameof(Archives))]
    public async Task Version_0_1_0_archives_list_verify_and_restore(string file, BackupKind kind, string artisan)
    {
        using var host = new TestHost();
        var launcher = host.CreateLauncher();
        Directory.CreateDirectory(host.BackupFolder);
        var archive = Path.Combine(host.BackupFolder, file);
        File.Copy(Path.Combine(Fixtures, file), archive);
        foreach (var plugin in new[] { "Artisan", "AutoRetainer", "Pandora's Box" })
        {
            launcher.WritePluginConfig(plugin, "changed");
        }

        launcher.Write("changed", "pluginConfigs", "AutoRetainer", "profiles", "main.json");
        launcher.Write("changed", "dalamudConfig.json");
        launcher.Write("changed", "dalamudUI.ini");

        var record = host.Catalog.Verify(Assert.Single(host.Catalog.List(host.BackupFolder)), Ct);
        Assert.Equal(kind, record.Kind);
        Assert.Equal(IntegrityState.Verified, record.Integrity);
        Assert.Equal(1, record.Manifest!.SchemaVersion);

        var result = await host.Restores.RestoreAsync(new RestoreRequest(archive), cancellationToken: Ct);

        Assert.Equal(3, result.PluginConfigCount);
        Assert.True(result.DalamudVfsRestored);
        Assert.Equal(artisan, launcher.ReadPluginConfig("Artisan"));
        Assert.Equal("""{ "Version": 1, "Plugin": "AutoRetainer" }""", launcher.ReadPluginConfig("AutoRetainer"));
        Assert.Equal("""{ "Version": 1, "Plugin": "Pandora's Box" }""", launcher.ReadPluginConfig("Pandora's Box"));
        Assert.Equal("""{ "Retainers": 10 }""", launcher.Read("pluginConfigs", "AutoRetainer", "profiles", "main.json"));
        Assert.Equal("""{ "LanguageOverride": "en", "ThirdRepoList": [] }""", launcher.Read("dalamudConfig.json"));
        Assert.Equal("[Window][Debug##Default]\nPos=60,60\n", launcher.Read("dalamudUI.ini"));
    }
}

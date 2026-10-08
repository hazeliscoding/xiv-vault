using System.IO.Compression;
using XivVault.Core;
using XivVault.Core.Backup;
using XivVault.Core.Restore;
using XivVault.Tests.Support;

namespace XivVault.Tests.Core;

/// <summary>Restoring only some plugins, or only Dalamud settings.</summary>
public class SelectiveRestoreTests
{
    private const string Changed = """{ "changed": true }""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Restoring_one_plugin_leaves_every_other_plugin_and_Dalamud_settings_as_they_were()
    {
        using var host = new TestHost();
        var launcher = host.CreateLauncher();
        var original = launcher.ReadPluginConfig("Artisan");
        var backup = await host.BackUpAsync();
        launcher.WritePluginConfig("Artisan", Changed);
        launcher.WritePluginConfig("Splatoon", Changed);
        launcher.Write(Changed, "pluginConfigs", "AutoRetainer", "profiles", "main.json");
        launcher.Write(Changed, "dalamudConfig.json");

        var result = await RestoreAsync(host, backup, RestoreSelection.Only(["Artisan"], dalamudSettings: false));

        Assert.Equal(original, launcher.ReadPluginConfig("Artisan"));
        Assert.Equal(Changed, launcher.ReadPluginConfig("Splatoon"));
        Assert.Equal(Changed, launcher.Read("pluginConfigs", "AutoRetainer", "profiles", "main.json"));
        Assert.Equal(Changed, launcher.Read("dalamudConfig.json"));
        Assert.Equal(1, result.PluginConfigCount);
        Assert.False(result.DalamudConfigRestored);
    }

    [Fact]
    public async Task A_plugin_is_restored_with_its_folder_and_matched_without_regard_to_case()
    {
        using var host = new TestHost();
        var launcher = host.CreateLauncher();
        var originalFile = launcher.ReadPluginConfig("AutoRetainer");
        var originalFolder = launcher.Read("pluginConfigs", "AutoRetainer", "profiles", "main.json");
        var backup = await host.BackUpAsync();
        launcher.WritePluginConfig("AutoRetainer", Changed);
        launcher.Write(Changed, "pluginConfigs", "AutoRetainer", "profiles", "main.json");

        await RestoreAsync(host, backup, RestoreSelection.Only(["autoretainer"], dalamudSettings: false));

        Assert.Equal(originalFile, launcher.ReadPluginConfig("AutoRetainer"));
        Assert.Equal(originalFolder, launcher.Read("pluginConfigs", "AutoRetainer", "profiles", "main.json"));
    }

    [Fact]
    public async Task Restoring_only_Dalamud_settings_leaves_every_plugin_as_it_was()
    {
        using var host = new TestHost();
        var launcher = host.CreateLauncher();
        var originalConfig = launcher.Read("dalamudConfig.json");
        var backup = await host.BackUpAsync();
        launcher.WritePluginConfig("Artisan", Changed);
        launcher.Write(Changed, "dalamudConfig.json");

        var result = await RestoreAsync(host, backup, RestoreSelection.Only([], dalamudSettings: true));

        Assert.Equal(originalConfig, launcher.Read("dalamudConfig.json"));
        Assert.Equal(Changed, launcher.ReadPluginConfig("Artisan"));
        Assert.Equal(0, result.PluginConfigCount);
        Assert.True(result.DalamudConfigRestored);
    }

    [Fact]
    public async Task The_safety_backup_still_holds_everything_a_full_restore_would_replace()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var backup = await host.BackUpAsync();

        var result = await RestoreAsync(host, backup, RestoreSelection.Only(["Artisan"], dalamudSettings: false));

        var snapshot = result.SafetySnapshot.Manifest!;
        Assert.Equal(FakeXivLauncher.DefaultPlugins.Length, snapshot.Statistics.PluginConfigCount);
        Assert.True(snapshot.Contents.DalamudConfig);
        Assert.True(snapshot.Contents.DalamudVfs);
        Assert.True(snapshot.Contents.DalamudUi);
    }

    [Fact]
    public async Task A_partial_restore_still_refuses_a_backup_damaged_outside_the_chosen_plugins()
    {
        using var host = new TestHost();
        var launcher = host.CreateLauncher();
        var backup = await host.BackUpAsync();
        IReadOnlyList<string> damagedPlugin;
        using (var zip = ZipFile.OpenRead(backup.Record.FilePath))
        {
            // Corrupt damages the first payload entry; choose a plugin that doesn't own it.
            var first = zip.Entries.First(entry => entry.FullName.StartsWith(BackupAllowlist.PayloadPrefix, StringComparison.Ordinal)).FullName;
            damagedPlugin = BackupAllowlist.PluginNames([first]);
        }

        RetentionTests.Corrupt(backup.Record.FilePath);
        var chosen = FakeXivLauncher.DefaultPlugins.First(plugin => !damagedPlugin.Contains(plugin));
        launcher.WritePluginConfig(chosen, Changed);

        var error = await Assert.ThrowsAsync<XivVaultException>(() => RestoreAsync(host, backup, RestoreSelection.Only([chosen], dalamudSettings: false)));

        Assert.Equal(XivVaultErrorKind.RestoreValidationFailed, error.Kind);
        Assert.Equal(Changed, launcher.ReadPluginConfig(chosen));
    }

    [Fact]
    public async Task A_plugin_the_backup_does_not_hold_is_refused_before_anything_changes()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var backup = await host.BackUpAsync();

        var error = await Assert.ThrowsAsync<XivVaultException>(
            () => RestoreAsync(host, backup, RestoreSelection.Only(["Artisan", "Not Installed"], dalamudSettings: false)));

        Assert.Equal(XivVaultErrorKind.InvalidConfiguration, error.Kind);
        Assert.Contains("Not Installed", error.Message);
        Assert.Empty(Directory.GetFiles(host.BackupFolder, BackupNaming.SafetyPrefix + "*"));
    }

    [Fact]
    public async Task Choosing_nothing_is_refused_before_anything_changes()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var backup = await host.BackUpAsync();

        var error = await Assert.ThrowsAsync<XivVaultException>(() => RestoreAsync(host, backup, RestoreSelection.Only([], dalamudSettings: false)));

        Assert.Equal(XivVaultErrorKind.InvalidConfiguration, error.Kind);
        Assert.Empty(Directory.GetFiles(host.BackupFolder, BackupNaming.SafetyPrefix + "*"));
    }

    [Fact]
    public async Task The_preview_counts_only_changes_the_chosen_parts_would_undo()
    {
        using var host = new TestHost();
        var launcher = host.CreateLauncher();
        var backup = await host.BackUpAsync();
        var later = host.Clock.Now.UtcDateTime.AddHours(1);
        launcher.WritePluginConfig("Splatoon", Changed);
        File.SetLastWriteTimeUtc(Path.Combine(launcher.PluginConfigs, "Splatoon.json"), later);
        launcher.Write(Changed, "dalamudConfig.json");
        File.SetLastWriteTimeUtc(Path.Combine(launcher.DataPath, "dalamudConfig.json"), later);

        var preview = await host.Restores.PreviewAsync(backup.Record.FilePath, cancellationToken: Ct);

        var everything = preview.ChangesUndoneBy(RestoreSelection.Everything);
        Assert.Equal(["Splatoon"], everything.Plugins);
        Assert.True(everything.DalamudConfig);
        var artisanOnly = preview.ChangesUndoneBy(RestoreSelection.Only(["Artisan"], dalamudSettings: false));
        Assert.Empty(artisanOnly.Plugins);
        Assert.False(artisanOnly.DalamudConfig);
        Assert.False(artisanOnly.Any);
    }

    private static Task<RestoreResult> RestoreAsync(TestHost host, BackupResult backup, RestoreSelection selection) =>
        host.Restores.RestoreAsync(new RestoreRequest(backup.Record.FilePath) { Selection = selection }, cancellationToken: Ct);
}

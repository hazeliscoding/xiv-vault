using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using XivVault.Core;
using XivVault.Core.Backup;
using XivVault.Core.Configuration;
using XivVault.Core.Discovery;
using XivVault.Core.Platform;
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

    [Fact]
    public async Task A_full_restore_of_a_backup_that_holds_nothing_still_succeeds()
    {
        // Restoring onto a PC where Dalamud never ran leaves an empty safety backup, which stays restorable.
        using var host = new TestHost();
        var oldPc = FakeXivLauncher.Create(Path.Combine(host.Environment.Root, "OldPC", "XIVLauncher"));
        var backup = await host.Backups.CreateBackupAsync(new BackupRequest(BackupKind.Manual) { Source = oldPc.Root }, cancellationToken: Ct);
        Directory.CreateDirectory(host.Environment.XivLauncherPath);
        File.WriteAllText(Path.Combine(host.Environment.XivLauncherPath, "launcherConfigV3.json"), "{}");
        var first = await host.Restores.RestoreAsync(new RestoreRequest(backup.Record.FilePath), cancellationToken: Ct);
        host.Clock.Advance(TimeSpan.FromMinutes(1));

        var result = await host.Restores.RestoreAsync(new RestoreRequest(first.SafetySnapshot.FilePath), cancellationToken: Ct);

        Assert.Equal(0, result.RestoredFileCount);
    }

    [Fact]
    public async Task Restoring_Dalamud_settings_from_a_backup_without_them_says_so()
    {
        using var host = new TestHost();
        var launcher = host.CreateLauncher();
        foreach (var file in new[] { "dalamudConfig.json", "dalamudVfs.db", "dalamudUI.ini" })
        {
            File.Delete(Path.Combine(launcher.DataPath, file));
        }

        var backup = await host.BackUpAsync();

        var error = await Assert.ThrowsAsync<XivVaultException>(() => RestoreAsync(host, backup, RestoreSelection.Only([], dalamudSettings: true)));

        Assert.Equal(XivVaultErrorKind.InvalidConfiguration, error.Kind);
        Assert.Equal("This backup has no Dalamud settings.", error.Message);
    }

    [Fact]
    public async Task A_partial_restore_that_fails_puts_back_only_what_it_changed()
    {
        // Artisan is written first, then dalamudConfig.json fails; Splatoon was never chosen.
        var writer = new RestoreTests.FailingWriter(failOnCall: 2);
        using var host = new TestHost(configure: services => services.AddSingleton<IRestoreService>(provider => new RestoreService(
            provider.GetRequiredService<IConfigStore>(),
            provider.GetRequiredService<IXivLauncherLocator>(),
            provider.GetRequiredService<ArchiveValidator>(),
            provider.GetRequiredService<BackupCatalog>(),
            provider.GetRequiredService<BackupService>(),
            provider.GetRequiredService<PortableStateScanner>(),
            provider.GetRequiredService<GameProcessGuard>(),
            provider.GetRequiredService<PathDisplay>(),
            provider.GetRequiredService<IAppEnvironment>(),
            provider.GetRequiredService<RetentionService>(),
            provider.GetRequiredService<OperationLock>(),
            NullLogger<RestoreService>.Instance,
            writer)));
        var launcher = host.CreateLauncher();
        var backup = await host.BackUpAsync();
        launcher.WritePluginConfig("Artisan", Changed);
        launcher.WritePluginConfig("Splatoon", Changed);
        launcher.Write(Changed, "dalamudConfig.json");

        var error = await Assert.ThrowsAsync<XivVaultException>(() => RestoreAsync(host, backup, RestoreSelection.Only(["Artisan"], dalamudSettings: true)));

        Assert.Contains("put back", error.Message);
        Assert.Equal(2, writer.Calls);
        Assert.Equal(Changed, launcher.ReadPluginConfig("Artisan"));
        Assert.Equal(Changed, launcher.ReadPluginConfig("Splatoon"));
        Assert.Equal(Changed, launcher.Read("dalamudConfig.json"));
    }

    [Fact]
    public async Task A_link_under_a_chosen_plugin_is_refused()
    {
        using var host = new TestHost();
        var launcher = host.CreateLauncher();
        var backup = await host.BackUpAsync();
        var elsewhere = LinkAutoRetainerElsewhere(host, launcher);

        var error = await Assert.ThrowsAsync<XivVaultException>(() => RestoreAsync(host, backup, RestoreSelection.Only(["AutoRetainer"], dalamudSettings: false)));

        Assert.Contains("link", error.Message);
        Assert.Equal(["keep.json"], Directory.GetFiles(elsewhere).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_link_under_a_plugin_that_is_not_chosen_does_not_block_the_restore()
    {
        using var host = new TestHost();
        var launcher = host.CreateLauncher();
        var original = launcher.ReadPluginConfig("Artisan");
        var backup = await host.BackUpAsync();
        var elsewhere = LinkAutoRetainerElsewhere(host, launcher);
        launcher.WritePluginConfig("Artisan", Changed);

        await RestoreAsync(host, backup, RestoreSelection.Only(["Artisan"], dalamudSettings: false));

        Assert.Equal(original, launcher.ReadPluginConfig("Artisan"));
        Assert.Equal(["keep.json"], Directory.GetFiles(elsewhere).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Plugins_with_dots_in_their_names_or_kept_only_in_a_folder_can_be_chosen()
    {
        using var host = new TestHost();
        var launcher = FakeXivLauncher.Create(host.Environment.XivLauncherPath, plugins: ["Dalamud.FindAnything", "Artisan"]);
        launcher.Write("{}", "pluginConfigs", "Artisan.json");
        Directory.CreateDirectory(Path.Combine(launcher.PluginConfigs, "FolderOnly"));
        launcher.Write("original", "pluginConfigs", "FolderOnly", "data.json");
        var backup = await host.BackUpAsync();
        Assert.Contains("FolderOnly", backup.Record.PluginNames);
        launcher.WritePluginConfig("Dalamud.FindAnything", Changed);
        launcher.Write(Changed, "pluginConfigs", "FolderOnly", "data.json");
        launcher.WritePluginConfig("Artisan", Changed);

        await RestoreAsync(host, backup, RestoreSelection.Only(["Dalamud.FindAnything", "FolderOnly"], dalamudSettings: false));

        Assert.NotEqual(Changed, launcher.ReadPluginConfig("Dalamud.FindAnything"));
        Assert.Equal("original", launcher.Read("pluginConfigs", "FolderOnly", "data.json"));
        Assert.Equal(Changed, launcher.ReadPluginConfig("Artisan"));
    }

    /// <summary>Replaces pluginConfigs\AutoRetainer with a junction to a folder outside XIVLauncher.</summary>
    private static string LinkAutoRetainerElsewhere(TestHost host, FakeXivLauncher launcher)
    {
        var elsewhere = Path.Combine(host.Environment.Root, "Elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "keep.json"), "outside");
        var linked = Path.Combine(launcher.PluginConfigs, "AutoRetainer");
        Directory.Delete(linked, recursive: true);
        RestoreHardeningTests.CreateJunction(linked, elsewhere);
        return elsewhere;
    }

    private static Task<RestoreResult> RestoreAsync(TestHost host, BackupResult backup, RestoreSelection selection) =>
        host.Restores.RestoreAsync(new RestoreRequest(backup.Record.FilePath) { Selection = selection }, cancellationToken: Ct);
}

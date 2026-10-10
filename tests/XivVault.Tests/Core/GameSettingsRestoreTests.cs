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

/// <summary>Restoring the game's own settings into the game's settings folder.</summary>
public class GameSettingsRestoreTests
{
    private const string Changed = "changed after the backup";
    private const string ContentId = "004000174A1B2C3D";

    private static readonly string First = FakeGameSettings.Characters[0];
    private static readonly string Second = FakeGameSettings.Characters[1];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<RestoreResult> RestoreAsync(TestHost host, BackupResult backup, RestoreSelection? selection = null) =>
        host.Restores.RestoreAsync(new RestoreRequest(backup.Record.FilePath) { Selection = selection ?? RestoreSelection.Everything }, cancellationToken: Ct);

    [Fact]
    public async Task Each_character_gets_its_own_settings_back()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var game = host.CreateGameSettings();
        var backup = await host.BackUpAsync();
        game.Write(Changed, First, "HOTBAR.DAT");
        game.Write(Changed, Second, "HOTBAR.DAT");
        game.Write(Changed, "MACROSYS.dat");
        game.Write(Changed, "FFXIV.cfg");

        var result = await RestoreAsync(host, backup);

        Assert.Equal($"HOTBAR.DAT of {First}", game.Read(First, "HOTBAR.DAT"));
        Assert.Equal($"HOTBAR.DAT of {Second}", game.Read(Second, "HOTBAR.DAT"));
        Assert.Equal("shared macros", game.Read("MACROSYS.dat"));
        Assert.StartsWith("<FINAL FANTASY XIV Config File>", game.Read("FFXIV.cfg"));
        Assert.True(result.GameSettingsRestored);
        Assert.True(result.GameConfigRestored);
        Assert.Equal(backup.Manifest.Files.Count, result.RestoredFileCount);
    }

    [Fact]
    public async Task Characters_and_files_the_backup_does_not_hold_stay()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var game = host.CreateGameSettings();
        var backup = await host.BackUpAsync();
        game.Write("a character made after the backup", "FFXIV_CHR0040003C0FFEE00", "HOTBAR.DAT");
        game.Write("added by a game patch", First, "NEWFEATURE.DAT");

        await RestoreAsync(host, backup);

        Assert.Equal("a character made after the backup", game.Read("FFXIV_CHR0040003C0FFEE00", "HOTBAR.DAT"));
        Assert.Equal("added by a game patch", game.Read(First, "NEWFEATURE.DAT"));
        Assert.Equal("[12:00] a private chat line", game.Read(First, "log", "00000000.log"));
        Assert.Equal("an older hotbar", game.Read(First, "HOTBAR.DAT.old"));
    }

    [Fact]
    public async Task On_a_PC_where_the_game_never_started_the_restore_creates_the_folder()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var game = host.CreateGameSettings();
        var backup = await host.BackUpAsync();
        Directory.Delete(Path.Combine(host.Environment.Documents, "My Games"), recursive: true);

        await RestoreAsync(host, backup);

        var restored = Directory.EnumerateFiles(game.Root, "*", SearchOption.AllDirectories)
            .Select(path => "payload/game/" + Path.GetRelativePath(game.Root, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);
        Assert.Equal(FakeGameSettings.ArchivePaths.Order(StringComparer.Ordinal), restored);
    }

    [Fact]
    public async Task Is_refused_while_the_game_runs_and_changes_no_game_file()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var game = host.CreateGameSettings();
        var backup = await host.BackUpAsync();
        game.Write(Changed, First, "HOTBAR.DAT");
        host.Processes.Running.Add("ffxiv_dx11");

        var error = await Assert.ThrowsAsync<XivVaultException>(() => RestoreAsync(host, backup));

        Assert.Equal(XivVaultErrorKind.GameRunning, error.Kind);
        Assert.Equal(Changed, game.Read(First, "HOTBAR.DAT"));
    }

    [Fact]
    public async Task A_failure_while_writing_game_settings_puts_both_folders_back()
    {
        var writer = new FailingWriter();
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
        var game = host.CreateGameSettings();
        var backup = await host.BackUpAsync();
        launcher.WritePluginConfig("Artisan", Changed);
        launcher.Write(Changed, "dalamudConfig.json");
        game.Write(Changed, "MACROSYS.dat");
        game.Write(Changed, First, "HOTBAR.DAT");

        // The second character's folder is missing, so the restore creates it. Game settings are
        // written last, so failing the last write leaves both folders to put back.
        Directory.Delete(Path.Combine(game.Root, Second), recursive: true);
        var before = Snapshot(launcher.Root).Concat(Snapshot(game.Root)).ToList();
        writer.FailOnCall = backup.Manifest.Files.Count;

        var error = await Assert.ThrowsAsync<XivVaultException>(() => RestoreAsync(host, backup));

        Assert.Equal(XivVaultErrorKind.Unexpected, error.Kind);
        Assert.Contains("put back", error.Message);
        Assert.Equal(backup.Manifest.Files.Count, writer.Calls);
        Assert.False(Directory.Exists(Path.Combine(game.Root, Second)));
        Assert.Equal(before, Snapshot(launcher.Root).Concat(Snapshot(game.Root)).ToList());
    }

    [Fact]
    public async Task Refuses_to_write_through_a_linked_character_folder()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var game = host.CreateGameSettings();
        var backup = await host.BackUpAsync();
        var elsewhere = Path.Combine(host.Environment.Root, "elsewhere");
        Directory.Move(Path.Combine(game.Root, First), elsewhere);
        RestoreHardeningTests.CreateJunction(Path.Combine(game.Root, First), elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "HOTBAR.DAT"), Changed);

        var error = await Assert.ThrowsAsync<XivVaultException>(() => RestoreAsync(host, backup));

        Assert.Equal(XivVaultErrorKind.RestoreValidationFailed, error.Kind);
        Assert.DoesNotContain(ContentId, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Changed, File.ReadAllText(Path.Combine(elsewhere, "HOTBAR.DAT")));
    }

    [Fact]
    public async Task Choosing_only_Dalamud_settings_leaves_game_settings_as_they_are()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var game = host.CreateGameSettings();
        var backup = await host.BackUpAsync();
        game.Write(Changed, First, "HOTBAR.DAT");
        game.Write(Changed, "FFXIV.cfg");

        var result = await RestoreAsync(host, backup, RestoreSelection.Only([], dalamudSettings: true));

        Assert.Equal(Changed, game.Read(First, "HOTBAR.DAT"));
        Assert.Equal(Changed, game.Read("FFXIV.cfg"));
        Assert.False(result.GameSettingsRestored);
        Assert.False(result.GameConfigRestored);
    }

    [Fact]
    public async Task FFXIV_cfg_is_its_own_choice()
    {
        using var host = new TestHost();
        var launcher = host.CreateLauncher();
        var game = host.CreateGameSettings();
        var backup = await host.BackUpAsync();
        game.Write(Changed, First, "HOTBAR.DAT");
        game.Write(Changed, "FFXIV.cfg");
        launcher.Write(Changed, "dalamudConfig.json");

        await RestoreAsync(host, backup, RestoreSelection.Only([], dalamudSettings: false, gameSettings: true, gameConfig: false));

        Assert.Equal($"HOTBAR.DAT of {First}", game.Read(First, "HOTBAR.DAT"));
        Assert.Equal(Changed, game.Read("FFXIV.cfg"));
        Assert.Equal(Changed, launcher.Read("dalamudConfig.json"));

        game.Write(Changed, First, "HOTBAR.DAT");
        await RestoreAsync(host, backup, RestoreSelection.Only([], dalamudSettings: false, gameSettings: false, gameConfig: true));

        Assert.StartsWith("<FINAL FANTASY XIV Config File>", game.Read("FFXIV.cfg"));
        Assert.Equal(Changed, game.Read(First, "HOTBAR.DAT"));
    }

    [Fact]
    public async Task Choosing_game_settings_from_a_backup_without_them_is_refused()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var backup = await host.BackUpAsync();

        var error = await Assert.ThrowsAsync<XivVaultException>(
            () => RestoreAsync(host, backup, RestoreSelection.Only([], dalamudSettings: false, gameSettings: true)));

        Assert.Equal(XivVaultErrorKind.InvalidConfiguration, error.Kind);
        Assert.Equal("This backup has no game settings.", error.Message);
    }

    [Fact]
    public async Task The_review_counts_characters_and_shows_what_the_backup_holds()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.CreateGameSettings();
        var backup = await host.BackUpAsync();

        var preview = await host.Restores.PreviewAsync(backup.Record.FilePath, cancellationToken: Ct);

        Assert.True(preview.Contents.GameSettings);
        Assert.True(preview.Contents.GameConfig);
        Assert.Equal(2, preview.Contents.CharacterCount);
        Assert.Null(preview.ProblemWith(RestoreSelection.Only([], dalamudSettings: false, gameSettings: true)));
    }

    private static List<string> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => path + "|" + File.ReadAllText(path).GetHashCode(StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

    private sealed class FailingWriter : IRestoreFileWriter
    {
        private readonly RenameRestoreFileWriter _inner = new();

        public int FailOnCall { get; set; } = int.MaxValue;

        public int Calls { get; private set; }

        public void Replace(string stagedPath, string targetPath)
        {
            if (++Calls == FailOnCall)
            {
                throw new IOException("Simulated disk failure");
            }

            _inner.Replace(stagedPath, targetPath);
        }
    }
}

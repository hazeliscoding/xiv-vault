using System.IO.Compression;
using System.Security.Cryptography;
using XivVault.Core.Backup;
using XivVault.Core.Restore;
using XivVault.Tests.Support;

namespace XivVault.Tests.Core;

/// <summary>The game's own settings: HUD layout, hotbars, keybinds, macros and gear sets.</summary>
public class GameSettingsTests : IDisposable
{
    private const string Character = "FFXIV_CHR004000174A1B2C3D";
    private const string ContentId = "004000174A1B2C3D";

    private readonly TestEnvironment _env = new();
    private readonly ArchiveValidator _validator = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _env.Dispose();

    private ArchiveValidation Validate(ArchiveBuilder builder) =>
        _validator.Validate(builder.Save(Path.Combine(_env.Root, "test.zip")), verifyContents: true, Ct);

    private static List<string> GamePaths(BackupManifest manifest) => manifest.Files
        .Select(file => file.Path)
        .Where(path => path.StartsWith("payload/game/", StringComparison.Ordinal))
        .Order(StringComparer.Ordinal)
        .ToList();

    [Fact]
    public async Task Backs_up_the_game_allowlist_under_payload_game()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.CreateGameSettings();

        var manifest = (await host.BackUpAsync()).Manifest;

        Assert.Equal(FakeGameSettings.ArchivePaths.Order(StringComparer.Ordinal), GamePaths(manifest));
    }

    [Fact]
    public async Task Never_backs_up_chat_logs_screenshots_or_old_copies()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.CreateGameSettings();

        var record = (await host.BackUpAsync()).Record;
        using var zip = ZipFile.OpenRead(record.FilePath);
        var names = zip.Entries.Select(entry => entry.FullName).ToList();

        Assert.DoesNotContain(names, name => name.Contains("/log/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.EndsWith(".log", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("screenshots", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("cfgcopy", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.EndsWith(".old", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("FFXIV_BOOT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Game_files_are_copied_byte_for_byte()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var game = host.CreateGameSettings();

        var record = (await host.BackUpAsync()).Record;
        var listed = record.Manifest!.Files.Single(file => file.Path == $"payload/game/{Character}/HOTBAR.DAT");
        using var zip = ZipFile.OpenRead(record.FilePath);
        using var stored = new MemoryStream();
        zip.GetEntry(listed.Path)!.Open().CopyTo(stored);

        var original = game.ReadBytes(Character, "HOTBAR.DAT");
        Assert.Equal(original, stored.ToArray());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(original)), listed.Sha256);
    }

    [Fact]
    public async Task Writes_manifest_version_2_with_the_game_contents()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.CreateGameSettings();

        var manifest = (await host.BackUpAsync()).Manifest;

        Assert.Equal(2, manifest.SchemaVersion);
        Assert.True(manifest.Contents.GameSettings);
        Assert.True(manifest.Contents.GameConfig);
        Assert.Equal(2, manifest.Statistics.CharacterCount);
    }

    [Fact]
    public async Task A_PC_without_game_settings_still_backs_up_Dalamud_settings()
    {
        using var host = new TestHost();
        host.CreateLauncher();

        var result = await host.BackUpAsync();

        Assert.Equal(IntegrityState.Verified, result.Record.Integrity);
        Assert.Empty(GamePaths(result.Manifest));
        Assert.False(result.Manifest.Contents.GameSettings);
        Assert.False(result.Manifest.Contents.GameConfig);
        Assert.Equal(0, result.Manifest.Statistics.CharacterCount);
    }

    [Fact]
    public async Task A_linked_character_folder_is_not_followed()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var game = host.CreateGameSettings();
        var elsewhere = Path.Combine(host.Environment.Root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "HOTBAR.DAT"), "behind a link");
        RestoreHardeningTests.CreateJunction(Path.Combine(game.Root, "FFXIV_CHR0040009ABCDEF012"), elsewhere);

        var manifest = (await host.BackUpAsync()).Manifest;

        Assert.DoesNotContain(manifest.Files, file => file.Path.Contains("FFXIV_CHR0040009ABCDEF012", StringComparison.Ordinal));
        Assert.Equal(2, manifest.Statistics.CharacterCount);
    }

    [Fact]
    public async Task A_restore_never_writes_game_settings_into_the_XIVLauncher_folder()
    {
        using var host = new TestHost();
        var launcher = host.CreateLauncher();
        host.CreateGameSettings();
        var backup = await host.BackUpAsync();

        await host.Restores.RestoreAsync(new RestoreRequest(backup.Record.FilePath), cancellationToken: Ct);

        Assert.Empty(Directory.EnumerateFileSystemEntries(launcher.Root, "*FFXIV*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFileSystemEntries(launcher.Root, "MACROSYS.dat", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task The_safety_snapshot_holds_the_game_settings_too()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.CreateGameSettings();
        var backup = await host.BackUpAsync();

        var result = await host.Restores.RestoreAsync(new RestoreRequest(backup.Record.FilePath), cancellationToken: Ct);

        Assert.Equal(FakeGameSettings.ArchivePaths.Order(StringComparer.Ordinal), GamePaths(result.SafetySnapshot.Manifest!));
    }

    [Fact]
    public void A_version_2_archive_with_game_settings_is_valid()
    {
        var result = Validate(new ArchiveBuilder()
            .SchemaVersion(2)
            .File("payload/dalamudConfig.json", "{}")
            .File("payload/game/MACROSYS.dat", "macros")
            .File("payload/game/FFXIV.cfg", "config")
            .File("payload/game/FFXIV_CHARA_01.dat", "appearance")
            .File($"payload/game/{Character}/HOTBAR.DAT", "hotbars")
            .File($"payload/game/{Character}/keybind.dat", "keybinds"));

        Assert.True(result.IsValid, result.Summary);
        Assert.Equal(1, result.Manifest!.Statistics.CharacterCount);
    }

    [Theory]
    [InlineData($"payload/game/{Character}/log/00000000.log")]
    [InlineData($"payload/game/{Character}/HOTBAR.DAT.old")]
    [InlineData($"payload/game/{Character}/notes.txt")]
    [InlineData($"payload/game/{Character}/profiles/HOTBAR.DAT")]
    [InlineData("payload/game/FFXIV_CHRNOTHEX/HOTBAR.DAT")]
    [InlineData("payload/game/HOTBAR.DAT")]
    [InlineData("payload/game/FFXIV_BOOT.cfg")]
    [InlineData("payload/game/FFXIV.cfg.old")]
    [InlineData("payload/game/screenshots/ffxiv_01.png")]
    [InlineData("payload/game/cfgcopy/FFXIV_CFGCPY01.dat")]
    public void Game_files_outside_the_allowlist_are_rejected(string path)
    {
        var result = Validate(new ArchiveBuilder().SchemaVersion(2).File("payload/game/MACROSYS.dat", "macros").File(path, "not allowed"));

        Assert.Contains(result.Issues, issue => issue.Code == ArchiveIssueCode.NotAllowlisted);
    }

    [Fact]
    public void A_version_1_archive_cannot_hold_game_settings()
    {
        var result = Validate(new ArchiveBuilder().SchemaVersion(1).File("payload/dalamudConfig.json", "{}").File("payload/game/MACROSYS.dat", "macros"));

        Assert.Contains(result.Issues, issue => issue.Code == ArchiveIssueCode.NotAllowlisted);
    }

    [Fact]
    public void A_character_count_that_does_not_match_the_files_is_rejected()
    {
        var result = Validate(new ArchiveBuilder().SchemaVersion(2).File($"payload/game/{Character}/HOTBAR.DAT", "hotbars").ClaimCharacterCount(3));

        Assert.Equal(ArchiveIssueCode.MalformedManifest, Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void A_damaged_game_file_is_named_without_its_content_ID()
    {
        var result = Validate(new ArchiveBuilder().SchemaVersion(2).FileWithWrongHash($"payload/game/{Character}/HOTBAR.DAT", "hotbars"));

        var issue = Assert.Single(result.Issues);
        Assert.Equal(ArchiveIssueCode.ChecksumMismatch, issue.Code);
        Assert.Contains("HOTBAR.DAT", issue.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ContentId, issue.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ContentId, issue.EntryPath!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_refused_game_file_is_named_without_its_content_ID()
    {
        var result = Validate(new ArchiveBuilder()
            .SchemaVersion(2)
            .File("payload/game/MACROSYS.dat", "macros")
            .File($"payload/game/{Character}/log/00000000.log", "chat"));

        var issue = Assert.Single(result.Issues, issue => issue.Code == ArchiveIssueCode.NotAllowlisted);
        Assert.DoesNotContain(ContentId, issue.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ContentId, issue.EntryPath!, StringComparison.OrdinalIgnoreCase);
    }
}

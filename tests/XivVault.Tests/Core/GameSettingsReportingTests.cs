using XivVault.Core.Configuration;
using XivVault.Core.Diagnostics;
using XivVault.Core.Restore;
using XivVault.Core.Status;
using XivVault.Tests.Support;

namespace XivVault.Tests.Core;

/// <summary>The game settings switch, status and Diagnostics.</summary>
public class GameSettingsReportingTests
{
    private const string ContentId = "004000174A1B2C3D";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<DiagnosticReport> DoctorAsync(TestHost host) => host.Get<IDiagnosticsService>().RunAsync(Ct);

    private static DiagnosticGroup GameGroup(DiagnosticReport report) => report.Groups.Single(group => group.Area == DiagnosticArea.GameSettings);

    [Fact]
    public async Task Turning_game_settings_off_leaves_them_out_of_backups()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.CreateGameSettings();
        host.UpdateConfig(config => config with { IncludeGameSettings = false });

        var manifest = (await host.BackUpAsync()).Manifest;

        Assert.DoesNotContain(manifest.Files, file => file.Path.StartsWith("payload/game/", StringComparison.Ordinal));
        Assert.False(manifest.Contents.GameSettings);
        Assert.False(manifest.Contents.GameConfig);
    }

    [Fact]
    public async Task The_safety_snapshot_holds_game_settings_even_when_backups_leave_them_out()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.CreateGameSettings();
        host.UpdateConfig(config => config with { IncludeGameSettings = false });
        var backup = await host.BackUpAsync();

        var result = await host.Restores.RestoreAsync(new RestoreRequest(backup.Record.FilePath), cancellationToken: Ct);

        Assert.Equal(FakeGameSettings.ArchivePaths.Count, result.SafetySnapshot.Manifest!.Files.Count(file => file.Path.StartsWith("payload/game/", StringComparison.Ordinal)));
    }

    [Fact]
    public void Game_settings_are_on_by_default()
    {
        Assert.True(new XivVaultConfig().IncludeGameSettings);
    }

    [Fact]
    public async Task Status_counts_characters()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.CreateGameSettings();

        var status = await host.Get<IStatusService>().GetAsync(cancellationToken: Ct);

        Assert.True(status.Game.Found);
        Assert.Equal(2, status.Game.CharacterCount);
        Assert.True(status.Game.HasSystemSettings);
        Assert.Equal(5, status.Portable!.PluginConfigCount);
    }

    [Fact]
    public async Task Doctor_reports_game_settings_by_count_and_never_by_content_ID()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.CreateGameSettings();

        var report = await DoctorAsync(host);

        var game = GameGroup(report);
        Assert.Equal(DiagnosticStatus.Healthy, game.Status);
        Assert.Contains(game.Checks, check => check.Label == "2 characters found");
        Assert.DoesNotContain(ContentId, report.ToText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Doctor_suggests_starting_the_game_when_it_never_ran()
    {
        using var host = new TestHost();
        host.CreateLauncher();

        var game = GameGroup(await DoctorAsync(host));

        Assert.Equal(DiagnosticStatus.Warning, game.Status);
        Assert.Contains(game.Checks, check => check.Label == "No game settings on this PC yet");
    }

    [Fact]
    public async Task Doctor_suggests_turning_game_settings_on_when_they_are_off()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.CreateGameSettings();
        host.UpdateConfig(config => config with { IncludeGameSettings = false });

        var game = GameGroup(await DoctorAsync(host));

        Assert.Equal(DiagnosticStatus.Warning, game.Status);
        Assert.Contains(game.Checks, check => check.Label == "Game settings are not backed up");
    }

    [Fact]
    public async Task Doctor_warns_about_a_linked_character_folder_without_naming_it()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var game = host.CreateGameSettings();
        var elsewhere = Path.Combine(host.Environment.Root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        RestoreHardeningTests.CreateJunction(Path.Combine(game.Root, "FFXIV_CHR0040009ABCDEF012"), elsewhere);

        var report = await DoctorAsync(host);

        var group = GameGroup(report);
        Assert.Equal(DiagnosticStatus.Warning, group.Status);
        Assert.Contains(group.Checks, check => check.Label == "A character folder is a link");
        Assert.DoesNotContain("9ABCDEF012", report.ToText(), StringComparison.OrdinalIgnoreCase);
    }
}

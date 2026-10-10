using XivVault.Core.Configuration;
using XivVault.Desktop.ViewModels;
using XivVault.Tests.Support;

namespace XivVault.Tests.Desktop;

/// <summary>Character settings and system settings in the desktop app.</summary>
public class GameSettingsDesktopTests
{
    private const string Changed = "changed after the backup";

    private static readonly string First = FakeGameSettings.Characters[0];

    private static async Task<RestoreViewModel> ReviewAsync(DesktopTestHost host)
    {
        var restore = host.Get<RestoreViewModel>();
        await host.Session.RefreshAsync();
        restore.Begin(null);
        await restore.ContinueCommand.ExecuteAsync(null);
        Assert.True(restore.IsStep2);
        return restore;
    }

    [Fact]
    public async Task The_review_starts_with_character_and_system_settings_chosen()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        host.CreateGameSettings();
        await host.BackUpAsync();

        var restore = await ReviewAsync(host);

        Assert.True(restore.CharacterSettingsChosen);
        Assert.True(restore.SystemSettingsChosen);
        Assert.Equal("Character settings · 2 characters", restore.CharacterSettingsLabel);
        Assert.Equal("System settings · FFXIV.cfg", restore.SystemSettingsLabel);
        Assert.True(restore.Selection.IsEverything);
        // The files carry today's date and the test clock another, so only the start is fixed.
        Assert.Contains(restore.Current, item => item.Label == "Character settings" && item.Meta.StartsWith("2 characters · changed ", StringComparison.Ordinal));
        Assert.Contains(restore.Current, item => item.Label == "System settings" && item.Meta.StartsWith("changed ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Leaving_out_system_settings_keeps_FFXIV_cfg_as_it_is()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        var game = host.CreateGameSettings();
        await host.BackUpAsync();
        game.Write(Changed, First, "HOTBAR.DAT");
        game.Write(Changed, "FFXIV.cfg");
        var restore = await ReviewAsync(host);

        restore.SystemSettingsChosen = false;
        await restore.ContinueCommand.ExecuteAsync(null);
        await restore.RestoreNowCommand.ExecuteAsync(null);

        Assert.Equal($"HOTBAR.DAT of {First}", game.Read(First, "HOTBAR.DAT"));
        Assert.Equal(Changed, game.Read("FFXIV.cfg"));
        Assert.Equal("Character settings restored · system settings unchanged (not chosen)", restore.ResultGame);
        Assert.Contains(restore.Stages, stage => stage.Label == "Restoring character settings");
    }

    [Fact]
    public async Task The_summary_stays_short_when_every_setting_is_chosen()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        host.CreateGameSettings();
        await host.BackUpAsync();
        var restore = await ReviewAsync(host);
        Assert.StartsWith("Restores 5 plugin configurations and all settings from", restore.RestoreSummary);

        restore.SystemSettingsChosen = false;
        Assert.StartsWith("Restores 5 plugin configurations, Dalamud settings and character settings from", restore.RestoreSummary);
    }

    [Fact]
    public async Task A_backup_without_game_settings_offers_no_game_choices()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        await host.BackUpAsync();

        var restore = await ReviewAsync(host);

        Assert.False(restore.HasCharacterSettings);
        Assert.False(restore.HasSystemSettings);
        Assert.Equal("Character settings — not in this backup", restore.CharacterSettingsLabel);
        Assert.Equal("System settings — not in this backup", restore.SystemSettingsLabel);
        Assert.True(restore.Selection.IsEverything);
    }

    [Fact]
    public async Task The_settings_switch_turns_game_settings_off()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        var settings = host.Get<SettingsViewModel>();
        await settings.ActivateAsync();
        Assert.True(settings.IncludeGame);

        settings.IncludeGame = false;

        Assert.False(host.Get<IConfigStore>().Load().IncludeGameSettings);
    }

    [Fact]
    public async Task Overview_says_Dalamud_and_game_settings_were_found()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        host.CreateGameSettings();
        await host.BackUpAsync();
        var overview = host.Get<OverviewViewModel>();

        await host.Session.RefreshAsync();

        Assert.Contains(overview.StatusRow, item => item.Label == "Dalamud and game settings found");
    }

    [Fact]
    public async Task Inspecting_a_backup_lists_its_game_settings()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        host.CreateGameSettings();
        await host.BackUpAsync();
        var backups = host.Get<BackupsViewModel>();

        await backups.ActivateAsync();

        Assert.Equal("character settings for 2 characters · system settings", backups.Rows.Single().GameLine);
    }

    [Fact]
    public async Task Diagnostics_shows_game_settings_with_their_own_icon()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        host.CreateGameSettings();
        var diagnostics = host.Get<DiagnosticsViewModel>();

        await diagnostics.ActivateAsync();

        Assert.Equal("Gamepad2", diagnostics.Groups.Single(group => group.Title == "Game settings").Icon);
    }
}

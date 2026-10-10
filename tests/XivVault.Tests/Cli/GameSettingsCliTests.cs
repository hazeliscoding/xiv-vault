using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Testing;
using XivVault.Cli;
using XivVault.Core.Platform;
using XivVault.Tests.Support;

namespace XivVault.Tests.Cli;

/// <summary>Character settings and system settings on the command line.</summary>
public class GameSettingsCliTests
{
    private const string Changed = "changed after the backup";

    private static readonly string First = FakeGameSettings.Characters[0];

    private static async Task<(int Code, string Output)> Run(TestHost host, params string[] args)
    {
        var console = new TestConsole();
        console.Profile.Width = 200;
        var code = await XivVaultCli.RunAsync(
            args,
            services =>
            {
                services.AddSingleton<IAppEnvironment>(host.Environment);
                services.AddSingleton<IProcessInspector>(host.Processes);
                services.AddSingleton<ICommandRunner>(host.Commands);
                services.AddSingleton<TimeProvider>(host.Clock);
                services.AddSingleton<IFileAvailability>(host.Files);
            },
            console,
            TestContext.Current.CancellationToken);
        return (code, console.Output);
    }

    [Fact]
    public async Task Backup_says_it_saved_game_settings()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.CreateGameSettings();

        var (code, output) = await Run(host, "backup");

        Assert.Equal(0, code);
        Assert.Contains("Backed up 5 plugin configurations, Dalamud settings and game settings for 2 characters", output);
    }

    [Fact]
    public async Task Restore_shows_the_character_and_system_settings_in_the_backup()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.CreateGameSettings();
        await host.BackUpAsync();

        var (code, output) = await Run(host, "restore", "latest", "--yes");

        Assert.Equal(0, code);
        Assert.Contains("Character settings · 2 characters", output);
        Assert.Contains("System settings · FFXIV.cfg", output);
        Assert.Contains("character settings restored", output);
        Assert.Contains("system settings restored", output);
    }

    [Fact]
    public async Task Restore_with_character_settings_restores_only_them()
    {
        using var host = new TestHost();
        var launcher = host.CreateLauncher();
        var game = host.CreateGameSettings();
        await host.BackUpAsync();
        game.Write(Changed, First, "HOTBAR.DAT");
        game.Write(Changed, "FFXIV.cfg");
        launcher.Write(Changed, "dalamudConfig.json");
        launcher.WritePluginConfig("Artisan", Changed);

        var (code, output) = await Run(host, "restore", "latest", "--yes", "--character-settings");

        Assert.Equal(0, code);
        Assert.Equal($"HOTBAR.DAT of {First}", game.Read(First, "HOTBAR.DAT"));
        Assert.Equal(Changed, game.Read("FFXIV.cfg"));
        Assert.Equal(Changed, launcher.Read("dalamudConfig.json"));
        Assert.Equal(Changed, launcher.ReadPluginConfig("Artisan"));
        Assert.Contains("System settings — not chosen", output);
    }

    [Fact]
    public async Task Restore_with_system_settings_restores_only_FFXIV_cfg()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var game = host.CreateGameSettings();
        await host.BackUpAsync();
        game.Write(Changed, First, "HOTBAR.DAT");
        game.Write(Changed, "FFXIV.cfg");

        var (code, _) = await Run(host, "restore", "latest", "--yes", "--system-settings");

        Assert.Equal(0, code);
        Assert.StartsWith("<FINAL FANTASY XIV Config File>", game.Read("FFXIV.cfg"));
        Assert.Equal(Changed, game.Read(First, "HOTBAR.DAT"));
    }

    [Fact]
    public async Task Restore_with_character_settings_from_a_backup_without_them_exits_with_2()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        await host.BackUpAsync();

        var (code, output) = await Run(host, "restore", "latest", "--yes", "--character-settings");

        Assert.Equal(2, code);
        Assert.Contains("This backup has no character settings.", output);
    }

    [Fact]
    public async Task Config_turns_game_settings_off_and_shows_it()
    {
        using var host = new TestHost();

        var (code, _) = await Run(host, "config", "set", "include-game", "no");
        var (_, shown) = await Run(host, "config", "show");

        Assert.Equal(0, code);
        Assert.False(host.Config.Load().IncludeGameSettings);
        Assert.Matches(@"include-game\s+no", shown);
    }

    [Fact]
    public async Task Status_counts_characters()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.CreateGameSettings();

        var (_, text) = await Run(host, "status");
        var (_, json) = await Run(host, "status", "--json");

        Assert.Matches(@"Game settings\s+2 characters · system settings", text);
        var game = JsonDocument.Parse(json).RootElement.GetProperty("gameSettings");
        Assert.True(game.GetProperty("found").GetBoolean());
        Assert.True(game.GetProperty("included").GetBoolean());
        Assert.Equal(2, game.GetProperty("characterCount").GetInt32());
        Assert.True(game.GetProperty("systemSettings").GetBoolean());
    }

    [Fact]
    public async Task Doctor_json_names_the_game_settings_group()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.CreateGameSettings();

        var (_, json) = await Run(host, "doctor", "--json");

        var areas = JsonDocument.Parse(json).RootElement.GetProperty("groups").EnumerateArray().Select(group => group.GetProperty("area").GetString());
        Assert.Equal(["xivLauncher", "dalamud", "gameSettings", "backupDestination", "scheduling"], areas);
    }

    [Fact]
    public async Task List_counts_characters_in_each_backup()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        host.CreateGameSettings();
        await host.BackUpAsync();

        var (_, text) = await Run(host, "list");
        var (_, json) = await Run(host, "list", "--json");

        Assert.Contains("Characters", text);
        var backup = JsonDocument.Parse(json).RootElement[0];
        Assert.Equal(2, backup.GetProperty("characterCount").GetInt32());
        Assert.True(backup.GetProperty("systemSettings").GetBoolean());
    }
}

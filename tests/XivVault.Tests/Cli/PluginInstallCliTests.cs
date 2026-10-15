using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Testing;
using XivVault.Cli;
using XivVault.Core;
using XivVault.Core.Platform;
using XivVault.Core.Plugins;
using XivVault.Core.Restore;
using XivVault.Tests.Support;
using static XivVault.Tests.Core.Plugins.RestorePluginInstallTests;

namespace XivVault.Tests.Cli;

/// <summary>
/// Plugin installs on the command line: only beta versions install plugins, only with --install-plugins, and no
/// version's help names the options while installs are in beta.
/// </summary>
public class PluginInstallCliTests
{
    private const string Ticked = "https://Raw.Example.com/Someone/Plugins/main/repo.json";
    private const string Unticked = "https://other.example.org/pluginmaster.json";
    private const string Disabled = "https://disabled.example.net/repo.json";
    private const string UntickedSameHost = "https://other.example.org/more/repo.json";

    private static async Task<(int Code, string Output)> Run(TestHost host, bool beta, params string[] args)
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
                services.AddSingleton<INotifier>(host.Notifications);
                services.AddSingleton<IWebDownloader>(host.Downloads);

                // Registered either way, so the stable cases also pass when the build itself is a beta.
                services.AddSingleton(new PluginInstalls(beta));
            },
            console,
            TestContext.Current.CancellationToken);
        return (code, console.Output);
    }

    private static string Phrase(PluginInstallOutcome outcome) => PluginOutcomeText.Describe(outcome);

    private static void AssertNothingChanged(TestHost host, string root)
    {
        Assert.Empty(host.Downloads.Requests);
        Assert.False(File.Exists(Path.Combine(root, "dalamudConfig.json")));
        Assert.False(Directory.Exists(Path.Combine(root, "installedPlugins")));
        Assert.Empty(Directory.GetFiles(host.BackupFolder, "pre-restore-*.zip"));
    }

    [Theory]
    [InlineData("--install-plugins")]
    [InlineData("--custom-repository", "raw.example.com")]
    [InlineData("--install-plugins", "--custom-repository", "raw.example.com")]
    public async Task A_stable_version_refuses_plugin_installs_and_requests_nothing(params string[] options)
    {
        using var host = new TestHost();
        await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit, Moodles], repositories: [(Ticked, true)]));
        var root = NewPc(host);
        var repository = Repository(host);
        repository.Add(Wotsit.Name);
        repository.Add(Moodles.Name, repository: Ticked);

        var (code, output) = await Run(host, beta: false, ["restore", "latest", "--yes", .. options]);

        Assert.Equal(2, code);
        Assert.Contains("Plugin installs are only in beta versions of XIV Vault.", output);
        AssertNothingChanged(host, root);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Help_never_names_the_plugin_install_options(bool beta)
    {
        using var host = new TestHost();

        var (_, restoreHelp) = await Run(host, beta, "restore", "--help");
        var (_, help) = await Run(host, beta, "--help");

        Assert.Contains("--dalamud-settings", restoreHelp);
        foreach (var option in new[] { "install-plugins", "custom-repository" })
        {
            Assert.DoesNotContain(option, restoreHelp + help, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_beta_installs_the_missing_plugins_and_lists_the_rest_for_xlplugins()
    {
        using var host = new TestHost();
        var config = FakePluginRepository.DalamudConfig([Wotsit, Orchestrion, Moodles, Lifestream, Tippy]);
        await BackUpOldPcAsync(host, config);
        var root = NewPc(host);
        InstallLocally(root, Tippy);
        var repository = Repository(host);
        repository.Add(Wotsit.Name);
        repository.Add(Orchestrion.Name);
        host.Downloads.Bodies.Remove(repository.Add(Moodles.Name));

        var (code, output) = await Run(host, beta: true, "restore", "latest", "--yes", "--install-plugins");

        Assert.Equal(0, code);
        Assert.Contains("✓ 4 missing plugins to look for: Lifestream, Moodles, Orchestrion and Wotsit", output);
        Assert.Contains("Restore complete", output);
        Assert.Contains("2 plugins installed: Orchestrion and Wotsit", output);
        Assert.Contains("1 plugin already installed", output);
        Assert.Contains("2 plugins not installed. Install them from Dalamud's plugin installer in the game (/xlplugins):", output);
        Assert.Contains($"Lifestream: {Phrase(PluginInstallOutcome.NotOffered)}", output);
        Assert.Contains($"Moodles: {Phrase(PluginInstallOutcome.DownloadFailed)}", output);
        Assert.Equal(config, File.ReadAllText(Path.Combine(root, "dalamudConfig.json")));
        foreach (var installed in new[] { Wotsit, Orchestrion })
        {
            Assert.True(File.Exists(Path.Combine(root, "installedPlugins", installed.Name, "1.0.0.0", installed.Name + ".json")));
        }
    }

    [Fact]
    public async Task A_beta_restore_without_install_plugins_requests_nothing_and_says_nothing_about_installs()
    {
        using var host = new TestHost();
        await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit]));
        var root = NewPc(host);
        Repository(host).Add(Wotsit.Name);

        var (code, output) = await Run(host, beta: true, "restore", "latest", "--yes");

        Assert.Equal(0, code);
        Assert.Contains("Restore complete", output);
        Assert.DoesNotContain("install", output, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(host.Downloads.Requests);
        Assert.False(Directory.Exists(Path.Combine(root, "installedPlugins")));
    }

    [Theory]
    [InlineData("Add --install-plugins to install plugins from a custom repository.", "--custom-repository", "raw.example.com")]
    [InlineData("Plugins are installed only with Dalamud settings. Add --dalamud-settings to include them.", "--install-plugins", "--plugin", "Wotsit")]
    [InlineData("Plugins are installed only with Dalamud settings. Add --dalamud-settings to include them.", "--install-plugins", "--character-settings")]
    public async Task A_beta_refuses_plugin_options_that_cannot_work_before_anything_changes(string message, params string[] options)
    {
        using var host = new TestHost();
        await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit, Moodles], repositories: [(Ticked, true)]), pluginConfigs: [Wotsit.Name]);
        var root = NewPc(host);
        var repository = Repository(host);
        repository.Add(Wotsit.Name);
        repository.Add(Moodles.Name, repository: Ticked);

        var (code, output) = await Run(host, beta: true, ["restore", "latest", "--yes", .. options]);

        Assert.Equal(2, code);
        Assert.Contains(message, output);
        AssertNothingChanged(host, root);
    }

    [Fact]
    public async Task A_custom_repository_the_backup_does_not_turn_on_is_refused_with_the_hosts_it_does()
    {
        using var host = new TestHost();
        await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit], repositories: [(Ticked, true), (Unticked, true), (Disabled, false)]));
        var root = NewPc(host);
        Repository(host).Add(Wotsit.Name);

        var (code, output) = await Run(host, beta: true, "restore", "latest", "--yes", "--install-plugins", "--custom-repository", "disabled.example.net");

        Assert.Equal(2, code);
        Assert.Contains("This backup's Dalamud settings don't turn on a custom repository at disabled.example.net.", output);
        Assert.Contains("Custom repositories this backup turns on: raw.example.com and other.example.org", output);
        AssertNothingChanged(host, root);
    }

    [Fact]
    public async Task Only_the_custom_repositories_named_by_host_are_installed_from()
    {
        using var host = new TestHost();
        await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit, Moodles, Lifestream], repositories: [(Ticked, true), (Unticked, true), (UntickedSameHost, true)]));
        var root = NewPc(host);
        var repository = Repository(host);
        repository.Add(Wotsit.Name);
        repository.Add(Moodles.Name, repository: Ticked);
        repository.Add(Lifestream.Name, repository: Unticked);

        var (code, output) = await Run(host, beta: true, "restore", "latest", "--yes", "--install-plugins", "--custom-repository", "RAW.example.COM");

        Assert.Equal(0, code);
        Assert.Contains("✓ 1 custom repository at raw.example.com · included", output);
        Assert.Contains("– 2 custom repositories at other.example.org · add --custom-repository other.example.org to include them", output);
        Assert.Contains("! Dalamud warns: \"Plugins have full control over your PC, like any other program, and may cause harm or crashes.", output);
        Assert.Contains("Please make absolutely sure that you only install plugins from developers you trust.", output);
        Assert.Contains("2 plugins installed: Moodles and Wotsit", output);
        Assert.Contains("! 1 plugin not installed. Install it from Dalamud's plugin installer in the game (/xlplugins):", output);
        Assert.Contains($"Lifestream: {Phrase(PluginInstallOutcome.NotOffered)}", output);
        Assert.True(Directory.Exists(Path.Combine(root, "installedPlugins", Moodles.Name)));
        Assert.False(Directory.Exists(Path.Combine(root, "installedPlugins", Lifestream.Name)));
        Assert.DoesNotContain(host.Downloads.Requests, url => url.Contains("other.example.org", StringComparison.Ordinal));

        // A repository's URL can hold a token: the screen shows only its host, and the logs name no repository or plugin.
        var logs = string.Concat(Directory.GetFiles(Path.Combine(host.Environment.DataDirectory, "logs")).Select(File.ReadAllText));
        Assert.NotEmpty(logs);
        foreach (var url in new[] { Ticked, Unticked, UntickedSameHost })
        {
            Assert.DoesNotContain(new Uri(url).AbsolutePath, output, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var secret in new[] { "example", Wotsit.Name, Moodles.Name, Lifestream.Name })
        {
            Assert.DoesNotContain(secret, logs, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task With_no_plugin_missing_the_review_says_so_and_nothing_is_requested()
    {
        using var host = new TestHost();
        await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit], repositories: [(Ticked, true)]));
        InstallLocally(NewPc(host), Wotsit);

        var (code, output) = await Run(host, beta: true, "restore", "latest", "--yes", "--install-plugins", "--custom-repository", "raw.example.com");

        Assert.Equal(0, code);
        Assert.Contains("– No missing plugins to install", output);
        Assert.DoesNotContain("raw.example.com", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Dalamud warns", output);
        Assert.Contains("1 plugin already installed", output);
        Assert.DoesNotContain("plugins installed", output);
        Assert.DoesNotContain("not installed", output);
        Assert.Empty(host.Downloads.Requests);
    }

    [Fact]
    public async Task Without_Dalamud_the_restore_says_once_to_restore_again_and_requests_nothing()
    {
        using var host = new TestHost();
        var config = FakePluginRepository.DalamudConfig([Wotsit, Moodles]);
        await BackUpOldPcAsync(host, config);
        var root = NewPc(host, dalamudReady: false);
        Repository(host).Add(Wotsit.Name);
        var phrase = Phrase(PluginInstallOutcome.DalamudNotReady);
        var notReady = char.ToUpperInvariant(phrase[0]) + phrase[1..];

        var (code, output) = await Run(host, beta: true, "restore", "latest", "--yes", "--install-plugins");

        Assert.Equal(0, code);
        Assert.Contains("– " + notReady, output);
        Assert.DoesNotContain("to install", output);

        // Dalamud's own install would give each plugin a new ID, so none is sent to /xlplugins.
        var result = output[output.IndexOf("Restore complete", StringComparison.Ordinal)..];
        Assert.Equal("! " + notReady, Assert.Single(result.Split('\n'), line => line.Contains(notReady, StringComparison.Ordinal)).TrimEnd());
        Assert.DoesNotContain("plugins installed", result);
        Assert.DoesNotContain("/xlplugins", output);
        Assert.DoesNotContain(Wotsit.Name, output);
        Assert.DoesNotContain(Moodles.Name, output);
        Assert.Empty(host.Downloads.Requests);
        Assert.Equal(config, File.ReadAllText(Path.Combine(root, "dalamudConfig.json")));
    }

    [Fact]
    public async Task Without_Dalamud_a_plugin_in_the_way_is_still_listed_for_xlplugins()
    {
        using var host = new TestHost();
        await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit, Lifestream]));
        var root = NewPc(host, dalamudReady: false);
        InstallLocally(root, Lifestream);
        File.WriteAllText(Path.Combine(root, "installedPlugins", Lifestream.Name, ".broken"), "");

        var (code, output) = await Run(host, beta: true, "restore", "latest", "--yes", "--install-plugins");

        Assert.Equal(0, code);
        Assert.Contains("! 1 plugin not installed. Install it from Dalamud's plugin installer in the game (/xlplugins):", output);
        Assert.Contains($"Lifestream: {Phrase(PluginInstallOutcome.InTheWay)}", output);
        Assert.DoesNotContain(Wotsit.Name, output);
    }

    [Fact]
    public async Task The_review_lists_the_plugins_the_chosen_settings_install()
    {
        using var host = new TestHost();
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit, Orchestrion, Moodles]), pluginConfigs: [Wotsit.Name, Orchestrion.Name]);
        NewPc(host);
        var repository = Repository(host);
        foreach (var plugin in new[] { Wotsit, Orchestrion, Moodles })
        {
            repository.Add(plugin.Name);
        }

        var preview = await host.Restores.PreviewAsync(archive, cancellationToken: TestContext.Current.CancellationToken);
        var toInstall = preview.PluginsToInstall(RestoreSelection.Only(["wotsit"], dalamudSettings: true)).Order(StringComparer.OrdinalIgnoreCase).ToList();

        var (code, output) = await Run(host, beta: true, "restore", "latest", "--yes", "--install-plugins", "--dalamud-settings", "--plugin", "wotsit");

        Assert.Equal(0, code);
        Assert.Equal([Moodles.Name, Wotsit.Name], toInstall);
        Assert.Contains($"✓ {Formatting.Count(toInstall.Count, "missing plugin")} to look for: {Formatting.JoinWords(toInstall)}", output);
        Assert.DoesNotContain(Orchestrion.Name, output);
    }

    [Fact]
    public async Task A_backup_whose_plugins_cannot_be_read_restores_its_settings_and_installs_nothing()
    {
        using var host = new TestHost();
        await BackUpOldPcAsync(host, "not Dalamud's settings");
        var root = NewPc(host);
        Repository(host).Add(Wotsit.Name);

        var (code, output) = await Run(host, beta: true, "restore", "latest", "--yes", "--install-plugins");

        Assert.Equal(0, code);
        Assert.Contains("This restore installs no plugins.", output);
        Assert.Contains("Restore complete", output);
        Assert.DoesNotContain("plugins installed", output);
        Assert.Empty(host.Downloads.Requests);
        Assert.Equal("not Dalamud's settings", File.ReadAllText(Path.Combine(root, "dalamudConfig.json")));
    }
}

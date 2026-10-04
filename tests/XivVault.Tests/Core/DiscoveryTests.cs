using XivVault.Core.Discovery;
using XivVault.Tests.Support;

namespace XivVault.Tests.Core;

public class DiscoveryTests
{
    [Fact]
    public void Finds_the_standard_layout_in_AppData()
    {
        using var host = new TestHost();
        host.CreateLauncher();

        var result = host.Locator.Locate();

        Assert.Equal(LocatorStatus.Found, result.Status);
        var installation = result.Installation!;
        Assert.Equal(PortableLayout.Standard, installation.Layout);
        Assert.Equal(InstallationSource.KnownPath, installation.Source);
        Assert.Equal(host.Environment.XivLauncherPath, installation.DataPath);
        Assert.True(installation.Artifacts.PluginConfigs);
        Assert.True(installation.Artifacts.DalamudConfig);
        Assert.True(installation.Artifacts.DalamudVfs);
        Assert.True(installation.Artifacts.DalamudUi);
        Assert.True(installation.HasPortableConfiguration);
    }

    [Fact]
    public void Finds_the_dalamudUserData_layout()
    {
        using var host = new TestHost();
        var launcher = host.CreateLauncher(userDataLayout: true);

        var installation = host.Locator.Locate().Installation!;

        Assert.Equal(PortableLayout.DalamudUserData, installation.Layout);
        Assert.Equal(launcher.DataPath, installation.DataPath);
        Assert.Equal(launcher.Root, installation.RootPath);
    }

    [Fact]
    public void Explicit_override_wins_over_the_detected_folder()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var custom = FakeXivLauncher.Create(Path.Combine(host.Environment.Root, "Games", "XIVLauncher"));

        var result = host.Locator.Locate(custom.Root);

        Assert.Equal(InstallationSource.Override, result.Installation!.Source);
        Assert.Equal(custom.Root, result.Installation.RootPath);
    }

    [Fact]
    public void Configured_override_is_used_when_no_explicit_path_is_given()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var custom = FakeXivLauncher.Create(Path.Combine(host.Environment.Root, "Games", "XIVLauncher"));
        host.UpdateConfig(config => config with { XivLauncherPathOverride = custom.Root });

        Assert.Equal(custom.Root, host.Locator.Locate().Installation!.RootPath);
    }

    [Fact]
    public void A_bad_override_is_reported_instead_of_falling_back_to_detection()
    {
        using var host = new TestHost();
        host.CreateLauncher();

        var result = host.Locator.Locate(Path.Combine(host.Environment.Root, "nowhere"));

        Assert.Equal(LocatorStatus.NotFound, result.Status);
        Assert.Null(result.Installation);
    }

    [Fact]
    public void Missing_installation_is_not_found()
    {
        using var host = new TestHost();

        var result = host.Locator.Locate();

        Assert.Equal(LocatorStatus.NotFound, result.Status);
        Assert.Contains(host.Environment.XivLauncherPath, result.CheckedPaths);
    }

    [Fact]
    public void An_empty_folder_named_XIVLauncher_is_invalid()
    {
        using var host = new TestHost();
        Directory.CreateDirectory(host.Environment.XivLauncherPath);
        File.WriteAllText(Path.Combine(host.Environment.XivLauncherPath, "notes.txt"), "not a launcher");

        var result = host.Locator.Locate();

        Assert.Equal(LocatorStatus.Invalid, result.Status);
        Assert.Null(result.Installation);
    }

    [Fact]
    public void A_fresh_launcher_without_Dalamud_config_is_found_but_has_nothing_to_back_up()
    {
        using var host = new TestHost();
        Directory.CreateDirectory(host.Environment.XivLauncherPath);
        File.WriteAllText(Path.Combine(host.Environment.XivLauncherPath, "launcherConfigV3.json"), "{}");

        var result = host.Locator.Locate();

        Assert.Equal(LocatorStatus.Found, result.Status);
        Assert.False(result.Installation!.HasPortableConfiguration);
    }

    [Fact]
    public void Reads_the_launcher_version_from_the_install_folder()
    {
        using var host = new TestHost();
        host.CreateLauncher();
        var install = Path.Combine(host.Environment.LocalAppData, "XIVLauncher");
        Directory.CreateDirectory(Path.Combine(install, "app-1.1.2"));
        Directory.CreateDirectory(Path.Combine(install, "app-1.0.9"));
        File.WriteAllText(Path.Combine(install, "XIVLauncher.exe"), "stub");

        var installation = host.Locator.Locate().Installation!;

        Assert.Equal(Path.Combine(install, "XIVLauncher.exe"), installation.LauncherExecutable);
        Assert.Equal("1.1.2", installation.LauncherVersion);
    }
}

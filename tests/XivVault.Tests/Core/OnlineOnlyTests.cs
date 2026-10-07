using XivVault.Core.Platform;
using XivVault.Tests.Support;

namespace XivVault.Tests.Core;

/// <summary>Backup folders synced by OneDrive and similar apps, where a file may be online only.</summary>
public class OnlineOnlyTests
{
    [Fact]
    public void A_file_whose_contents_are_not_on_this_pc_is_online_only()
    {
        using var environment = new TestEnvironment();
        var path = Path.Combine(environment.Root, "backup.zip");
        File.WriteAllText(path, "zip");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Offline);

        Assert.True(new WindowsFileAvailability().IsOnlineOnly(path));
    }

    [Fact]
    public void A_normal_or_missing_file_is_not_online_only()
    {
        using var environment = new TestEnvironment();
        var path = Path.Combine(environment.Root, "backup.zip");
        File.WriteAllText(path, "zip");

        var availability = new WindowsFileAvailability();

        Assert.False(availability.IsOnlineOnly(path));
        Assert.False(availability.IsOnlineOnly(Path.Combine(environment.Root, "missing.zip")));
    }
}

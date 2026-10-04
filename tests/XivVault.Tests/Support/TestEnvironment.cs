using XivVault.Core.Platform;

namespace XivVault.Tests.Support;

/// <summary>A throwaway user profile in a temp folder. Every test gets its own.</summary>
public sealed class TestEnvironment : IAppEnvironment, IDisposable
{
    public TestEnvironment(bool withOneDrive = true)
    {
        Root = Path.Combine(Path.GetTempPath(), "xiv-vault-tests", Guid.NewGuid().ToString("N")[..12]);
        UserProfile = Path.Combine(Root, "Users", "tester");
        RoamingAppData = Path.Combine(UserProfile, "AppData", "Roaming");
        LocalAppData = Path.Combine(UserProfile, "AppData", "Local");
        Documents = Path.Combine(UserProfile, "Documents");
        TempPath = Path.Combine(Root, "Temp");
        DataDirectory = Path.Combine(LocalAppData, "XIV Vault");
        OneDrive = withOneDrive ? Path.Combine(UserProfile, "OneDrive") : null;
        foreach (var folder in new[] { RoamingAppData, LocalAppData, Documents, TempPath, OneDrive })
        {
            if (folder is not null)
            {
                Directory.CreateDirectory(folder);
            }
        }
    }

    public string Root { get; }

    public string RoamingAppData { get; }

    public string LocalAppData { get; }

    public string UserProfile { get; }

    public string Documents { get; }

    public string? OneDrive { get; }

    public string TempPath { get; }

    public string UserAccount => @"TESTPC\tester";

    public string DataDirectory { get; }

    public string XivLauncherPath => Path.Combine(RoamingAppData, "XIVLauncher");

    public string DefaultBackupFolder => Path.Combine(OneDrive ?? Documents, "XIV Vault");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

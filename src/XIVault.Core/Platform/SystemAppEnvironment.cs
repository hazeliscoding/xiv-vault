namespace XIVault.Core.Platform;

public sealed class SystemAppEnvironment : IAppEnvironment
{
    /// <summary>Redirects config, state and logs, so smoke tests never touch the real profile.</summary>
    public const string DataDirectoryVariable = "XIVAULT_DATA_DIR";

    public string RoamingAppData { get; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public string LocalAppData { get; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public string UserProfile { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string Documents { get; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public string? OneDrive { get; } = ReadOneDrive();

    public string TempPath { get; } = Path.GetTempPath();

    public string UserAccount { get; } = $@"{Environment.UserDomainName}\{Environment.UserName}";

    public string DataDirectory => Environment.GetEnvironmentVariable(DataDirectoryVariable) is { Length: > 0 } custom
        ? Path.GetFullPath(custom)
        : Path.Combine(LocalAppData, "XIVault");

    private static string? ReadOneDrive()
    {
        foreach (var name in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
            {
                return value;
            }
        }

        return null;
    }
}

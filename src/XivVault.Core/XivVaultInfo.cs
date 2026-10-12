using System.Reflection;

namespace XivVault.Core;

public static class XivVaultInfo
{
    public const string RepositoryUrl = "https://github.com/hazeliscoding/xiv-vault";

    public static string Version { get; } = ReadVersion();

    /// <summary>True for a prerelease such as 1.1.0-beta.1, which release.yml builds from a beta tag.</summary>
    public static bool IsBeta { get; } = IsPrerelease(Version);

    internal static bool IsPrerelease(string version) => version.Contains('-');

    private static string ReadVersion()
    {
        var informational = typeof(XivVaultInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return typeof(XivVaultInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        // The SDK appends "+<commit>" to the informational version; users only need the release number.
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}

namespace XivVault.Core.Configuration;

public static class UpdateChannel
{
    public static bool Allows(string version, bool getBetaVersions) => getBetaVersions || !XivVaultInfo.IsPrerelease(version);
}

using XivVault.Core.Platform;

namespace XivVault.Core.Discovery;

/// <summary>
/// Where the game keeps its own settings. It is found through Documents, so a Documents folder that
/// OneDrive has moved is followed.
/// </summary>
public static class GameSettingsFolder
{
    public const string Name = "FINAL FANTASY XIV - A Realm Reborn";

    public static string In(IAppEnvironment environment) => Path.Combine(environment.Documents, "My Games", Name);
}

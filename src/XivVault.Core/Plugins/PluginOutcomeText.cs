namespace XivVault.Core.Plugins;

/// <summary>The short phrase both front ends show after a plugin's name, as in "Penumbra: installed".</summary>
public static class PluginOutcomeText
{
    public static string Describe(PluginInstallOutcome outcome) => outcome switch
    {
        PluginInstallOutcome.Installed => "installed",
        PluginInstallOutcome.AlreadyInstalled => "already installed",
        PluginInstallOutcome.InTheWay => "an unfinished or removed copy is in the way: start the game once, then restore again",
        PluginInstallOutcome.IdInUse => "another installed plugin uses its ID",
        PluginInstallOutcome.NotOffered => "not in the official repository or a custom repository you chose",
        PluginInstallOutcome.RepositoryUnavailable => "its repository couldn't be reached",
        PluginInstallOutcome.OfferedTwice => "offered by more than one custom repository you chose",
        PluginInstallOutcome.NotUpdated => "not updated for this version of Dalamud yet",
        PluginInstallOutcome.NeedsNewerDalamud => "needs a newer version of Dalamud",
        PluginInstallOutcome.NotForThisGameVersion => "made for a different game version",
        PluginInstallOutcome.Banned => "Dalamud blocks this version",
        PluginInstallOutcome.TestingOnly => "only a testing version is available",
        PluginInstallOutcome.DownloadFailed => "the download failed",
        PluginInstallOutcome.PackageRefused => "the download didn't pass XIV Vault's checks",
        PluginInstallOutcome.GameStarted => "the game or XIVLauncher started, so installing stopped",
        PluginInstallOutcome.WriteFailed => "it couldn't be written to the plugins folder",
        PluginInstallOutcome.DalamudNotReady => "Dalamud hasn't finished downloading: open XIVLauncher, let it finish, close it and restore again",
        PluginInstallOutcome.LinkedFolder => "the plugins folder is a link to another location",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };
}

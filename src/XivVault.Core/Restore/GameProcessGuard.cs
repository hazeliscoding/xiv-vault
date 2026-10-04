using XivVault.Core.Platform;

namespace XivVault.Core.Restore;

public sealed record RunningApps(IReadOnlyList<string> Launcher, IReadOnlyList<string> Game)
{
    public bool LauncherRunning => Launcher.Count > 0;

    public bool GameRunning => Game.Count > 0;

    public bool Any => LauncherRunning || GameRunning;
}

/// <summary>Detects XIVLauncher and FFXIV. Dalamud runs inside the game and rewrites its files while it runs.</summary>
public sealed class GameProcessGuard(IProcessInspector processes)
{
    public static readonly IReadOnlyList<string> LauncherProcesses = ["XIVLauncher", "XIVLauncherCN"];

    public static readonly IReadOnlyList<string> GameProcesses =
        ["ffxiv_dx11", "ffxiv", "ffxivboot", "ffxivboot64", "ffxivlauncher", "ffxivlauncher64"];

    public RunningApps Check() => new(processes.FindRunning(LauncherProcesses), processes.FindRunning(GameProcesses));
}

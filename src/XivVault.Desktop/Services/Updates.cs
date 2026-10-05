using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;
using XivVault.Core;
using XivVault.Core.Scheduling;

namespace XivVault.Desktop.Services;

public sealed record AvailableUpdate(string Version);

/// <summary>Self-update for a copy installed with Setup. Portable copies can't replace themselves.</summary>
public interface IAppUpdater
{
    bool IsInstalled { get; }

    /// <summary>Asks the release feed for a newer version. Null means this is the latest.</summary>
    Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default);

    Task DownloadAsync(AvailableUpdate update, Action<int> progress, CancellationToken cancellationToken = default);

    /// <summary>Exits XIV Vault, replaces its files with the downloaded version and starts it again.</summary>
    void RestartToApply(AvailableUpdate update);
}

public sealed class VelopackUpdater : IAppUpdater
{
    /// <summary>A folder or URL with a Velopack release feed, used instead of GitHub to try an update locally.</summary>
    public const string SourceVariable = "XIV_VAULT_UPDATE_SOURCE";

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);

    private readonly UpdateManager? _manager;
    private UpdateInfo? _latest;

    public VelopackUpdater(ILogger<VelopackUpdater> logger)
    {
        try
        {
            var source = Environment.GetEnvironmentVariable(SourceVariable);
            var manager = string.IsNullOrWhiteSpace(source)
                ? new UpdateManager(new GithubSource(XivVaultInfo.RepositoryUrl, accessToken: null, prerelease: false))
                : new UpdateManager(source);
            _manager = manager.IsInstalled ? manager : null;
        }
        catch (Exception ex)
        {
            // A build run from source or a portable zip has no install to update; that is not an error.
            logger.LogDebug(ex, "Updates are unavailable for this copy");
        }
    }

    public bool IsInstalled => _manager is not null;

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        _latest = await Manager.CheckForUpdatesAsync().WaitAsync(CheckTimeout, cancellationToken).ConfigureAwait(false);
        return _latest is null ? null : new AvailableUpdate(_latest.TargetFullRelease.Version.ToString());
    }

    public Task DownloadAsync(AvailableUpdate update, Action<int> progress, CancellationToken cancellationToken = default) =>
        Manager.DownloadUpdatesAsync(Find(update), progress, cancellationToken);

    public void RestartToApply(AvailableUpdate update) => Manager.ApplyUpdatesAndRestart(Find(update).TargetFullRelease);

    private UpdateManager Manager => _manager ?? throw new InvalidOperationException("This copy of XIV Vault was not installed with Setup.");

    private UpdateInfo Find(AvailableUpdate update) =>
        _latest is { } latest && latest.TargetFullRelease.Version.ToString() == update.Version
            ? latest
            : throw new InvalidOperationException($"Version {update.Version} was not found by the last update check.");
}

/// <summary>Other running copies of the desktop app, such as a scheduled backup started by Windows.</summary>
public interface IAppInstances
{
    bool OthersRunning { get; }
}

public sealed class ProcessAppInstances : IAppInstances
{
    public bool OthersRunning
    {
        get
        {
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) is not { Length: > 0 } name)
            {
                return false;
            }

            var processes = Process.GetProcessesByName(name);
            try
            {
                return processes.Any(process => process.Id != Environment.ProcessId);
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }
    }
}

/// <summary>
/// Runs while Setup's uninstaller removes XIV Vault. A scheduled task that starts this copy would
/// keep starting a program that no longer exists, so it is removed. The saved schedule
/// preferences stay, like turning automatic backups off.
/// </summary>
public sealed class UninstallCleanup(ScheduleService schedule, ILogger<UninstallCleanup> logger)
{
    public async Task RunAsync(string installFolder, CancellationToken cancellationToken = default)
    {
        var status = await schedule.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status is not { Installed: true, Command: { } command } || !IsInside(command.Executable, installFolder))
        {
            return;
        }

        await schedule.DisableAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Removed the scheduled backup task because XIV Vault is being uninstalled");
    }

    private static bool IsInside(string path, string folder)
    {
        if (!Path.IsPathFullyQualified(path) || !Path.IsPathFullyQualified(folder))
        {
            return false;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}

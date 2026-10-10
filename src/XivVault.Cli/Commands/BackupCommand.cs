using System.ComponentModel;
using Spectre.Console.Cli;
using XivVault.Cli.Infrastructure;
using XivVault.Core;
using XivVault.Core.Backup;
using XivVault.Core.Platform;
using XivVault.Core.Scheduling;
using XivVault.Core.State;

namespace XivVault.Cli.Commands;

internal sealed class BackupCommand(
    CliOutput output,
    IBackupService backups,
    ScheduledBackupRunner scheduledRunner,
    PathDisplay paths) : XivVaultCommand<BackupCommand.Settings>(output)
{
    internal sealed class Settings : GlobalSettings
    {
        [CommandOption("-q|--quiet")]
        [Description("Print nothing unless something goes wrong.")]
        public bool Quiet { get; init; }

        [CommandOption("-d|--destination <PATH>")]
        [Description("Back up to this folder instead of the configured one.")]
        public string? Destination { get; init; }

        [CommandOption("-s|--source <PATH>")]
        [Description("Back up this XIVLauncher folder instead of the detected one.")]
        public string? Source { get; init; }

        [CommandOption("--scheduled")]
        [Description("Run as the scheduled task: wait for FFXIV to close and record the result.")]
        public bool Scheduled { get; init; }
    }

    protected override async Task<int> RunAsync(Settings settings, CancellationToken cancellationToken)
    {
        Output.Quiet = settings.Quiet;
        if (settings.Scheduled)
        {
            var outcome = await scheduledRunner.RunAsync(cancellationToken: cancellationToken);
            if (outcome.Result == ScheduledRunResult.Success)
            {
                Report(outcome.Backup!);
                return 0;
            }

            Output.Error(outcome.Message);
            return (int)(outcome.Error ?? XivVaultErrorKind.Unexpected);
        }

        var request = new BackupRequest(BackupKind.Manual)
        {
            Destination = settings.Destination is null ? null : Path.GetFullPath(settings.Destination),
            Source = settings.Source is null ? null : Path.GetFullPath(settings.Source),
        };
        var result = await Output.WithStatusAsync("Scanning plugin configurations", update =>
            backups.CreateBackupAsync(request, new InlineProgress<BackupProgress>(progress => update($"{progress.Message} · {progress.Percent}%")), cancellationToken));
        Report(result);
        return 0;
    }

    private void Report(BackupResult result)
    {
        var record = result.Record;
        var manifest = result.Manifest;
        var what = new List<string> { Formatting.Count(manifest.Statistics.PluginConfigCount, "plugin configuration") };
        if (manifest.Contents.DalamudConfig)
        {
            what.Add("Dalamud settings");
        }

        if (manifest.Contents.CharacterSettings || manifest.Contents.SystemSettings)
        {
            var characters = manifest.Statistics.CharacterCount;
            what.Add(characters > 0 ? $"game settings for {Formatting.Count(characters, "character")}" : "game settings");
        }

        Output.Success($"Backed up {Formatting.JoinWords(what)}");
        Output.Detail($"{record.FileName} · {Formatting.Bytes(record.SizeBytes)} · verified sha256 {ArchiveValidator.Short(record.ArchiveSha256 ?? "")}");
        var saved = $"Saved to {paths.Friendly(Path.GetDirectoryName(record.FilePath)!)}";
        if (result.RemovedByRetention.Count > 0)
        {
            saved += $" · removed {Formatting.Count(result.RemovedByRetention.Count, "older backup")}";
        }

        Output.Detail(saved);
    }
}

/// <summary>Runs the callback on the reporting thread; Progress&lt;T&gt; would post it elsewhere and reorder updates.</summary>
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

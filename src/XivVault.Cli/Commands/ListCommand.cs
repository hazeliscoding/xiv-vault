using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using XivVault.Cli.Infrastructure;
using XivVault.Core;
using XivVault.Core.Backup;
using XivVault.Core.Configuration;
using XivVault.Core.Platform;

namespace XivVault.Cli.Commands;

internal sealed class ListCommand(
    CliOutput output,
    IConfigStore configStore,
    IBackupCatalog catalog,
    PathDisplay paths,
    TimeProvider clock) : XivVaultCommand<ListCommand.Settings>(output)
{
    internal sealed class Settings : GlobalSettings
    {
        [CommandOption("--json")]
        [Description("Print the backups as JSON.")]
        public bool Json { get; init; }

        [CommandOption("--verify")]
        [Description("Check every backup's hashes, not just the ones this PC hasn't seen.")]
        public bool Verify { get; init; }
    }

    protected override async Task<int> RunAsync(Settings settings, CancellationToken cancellationToken)
    {
        var config = configStore.Load();
        var destination = config.BackupDestination!;
        var records = await Task.Run(
            () => catalog.List(destination)
                .Select(record => record.HasManifest && (settings.Verify || record.Integrity == IntegrityState.Unverified)
                    ? catalog.Verify(record, cancellationToken)
                    : record)
                .ToList(),
            cancellationToken);

        if (settings.Json)
        {
            Output.Json(records.Select(JsonModels.From).ToList());
            return 0;
        }

        if (records.Count == 0)
        {
            Output.Line($"No backups in {paths.Friendly(destination)} yet. Run: xiv-vault backup");
            return 0;
        }

        var now = clock.GetLocalNow().DateTime;
        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey);
        table.AddColumns("Date", "Time", "Size", "Plugins", "Type", "Integrity", "File");
        table.Columns[2].RightAligned();
        table.Columns[3].RightAligned();
        table.Columns[6].NoWrap();
        foreach (var record in records)
        {
            var local = record.CreatedAtUtc.ToLocalTime();
            var integrity = record.Integrity switch
            {
                IntegrityState.Verified => $"[{CliOutput.Healthy}]Verified[/]",
                IntegrityState.Failed => $"[{CliOutput.Critical}]Failed[/]",
                _ => $"[{CliOutput.Dim}]Unverified[/]",
            };
            table.AddRow(
                Markup.Escape(Formatting.Day(local, now)),
                Markup.Escape(Formatting.Time(local)),
                Markup.Escape(Formatting.Bytes(record.SizeBytes)),
                record.PluginConfigCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Markup.Escape(Formatting.KindLabel(record.Kind)),
                integrity,
                $"[{CliOutput.Dim}]{Markup.Escape(record.FileName)}[/]");
        }

        Output.Write(table);
        Output.Detail($"{Formatting.Count(records.Count, "backup")} · {Formatting.Bytes(records.Sum(record => record.SizeBytes))} total · keeping latest {config.RetentionCount} · {paths.Friendly(destination)}");
        return records.Any(record => record.Integrity == IntegrityState.Failed) ? (int)XivVaultErrorKind.BackupValidationFailed : 0;
    }
}

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
        [Description("Check every backup's hashes, not just the latest. Backups stored only in the cloud are downloaded.")]
        public bool Verify { get; init; }
    }

    protected override async Task<int> RunAsync(Settings settings, CancellationToken cancellationToken)
    {
        var config = configStore.Load();
        var destination = config.BackupDestination!;
        var records = await Task.Run(
            () => settings.Verify
                ? catalog.List(destination).Select(record => record.IsRecognized ? catalog.Verify(record, cancellationToken) : record).ToList()
                : catalog.ListVerifyingLatest(destination, cancellationToken),
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
            var color = record.Integrity switch
            {
                IntegrityState.Verified => CliOutput.Healthy,
                IntegrityState.Failed => CliOutput.Critical,
                _ => CliOutput.Dim,
            };
            var integrity = $"[{color}]{Markup.Escape(Formatting.IntegrityLabel(record))}[/]";
            table.AddRow(
                Markup.Escape(Formatting.Day(local, now)),
                Markup.Escape(Formatting.Time(local)),
                Markup.Escape(Formatting.Bytes(record.SizeBytes)),
                record.HasManifest ? record.PluginConfigCount.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"[{CliOutput.Dim}]–[/]",
                Markup.Escape(Formatting.KindLabel(record)),
                integrity,
                $"[{CliOutput.Dim}]{Markup.Escape(record.FileName)}[/]");
        }

        Output.Write(table);
        Output.Detail($"{Formatting.Count(records.Count, "backup")} · {Formatting.Bytes(records.Sum(record => record.SizeBytes))} total · keeping latest {config.RetentionCount} · {paths.Friendly(destination)}");
        return records.Any(record => record.Integrity == IntegrityState.Failed) ? (int)XivVaultErrorKind.BackupValidationFailed : 0;
    }
}

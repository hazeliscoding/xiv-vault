using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using XIVault.Cli.Infrastructure;
using XIVault.Core;
using XIVault.Core.Backup;
using XIVault.Core.Configuration;
using XIVault.Core.Platform;
using XIVault.Core.Restore;

namespace XIVault.Cli.Commands;

internal sealed class RestoreCommand(
    CliOutput output,
    IConfigStore configStore,
    IBackupCatalog catalog,
    IRestoreService restores,
    PathDisplay paths,
    TimeProvider clock) : XivaultCommand<RestoreCommand.Settings>(output)
{
    internal sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[BACKUP]")]
        [Description("\"latest\", a file name in the backup folder, or a path to a backup. Leave it out to choose from a list.")]
        public string? Backup { get; init; }

        [CommandOption("-y|--yes")]
        [Description("Restore without asking for confirmation.")]
        public bool Yes { get; init; }

        [CommandOption("-s|--source <PATH>")]
        [Description("Restore into this XIVLauncher folder instead of the detected one.")]
        public string? Source { get; init; }
    }

    protected override async Task<int> RunAsync(Settings settings, CancellationToken cancellationToken)
    {
        var source = settings.Source is null ? null : Path.GetFullPath(settings.Source);
        var record = Choose(settings.Backup);
        var preview = await restores.PreviewAsync(record.FilePath, source, cancellationToken);
        var now = clock.GetLocalNow().DateTime;
        var created = preview.Backup.CreatedAtUtc.ToLocalTime();

        Output.Markup($"[bold]{Markup.Escape(Formatting.FullDate(created))}[/] [{CliOutput.Dim}]· {Markup.Escape(Formatting.KindLabel(preview.Backup.Kind))} · {Markup.Escape(Formatting.Age(created, now))}[/]");
        var contents = preview.Contents;
        Item(true, Formatting.Count(contents.PluginConfigCount, "plugin configuration"));
        Item(contents.DalamudConfig, "Dalamud settings");
        Item(contents.DalamudVfs, "Plugin collection database");
        Item(contents.DalamudConfig, contents.CustomRepositoryCount is { } repos
            ? $"Custom repository settings · {Formatting.Count(repos, "repo")}"
            : "Custom repository settings");
        Item(contents.DalamudUi, contents.DalamudUi ? "UI layout" : "UI layout — not included");
        if (preview.IsOlderThanCurrent)
        {
            var plugins = preview.PluginsChangedSinceBackup;
            Output.Warning(plugins.Count > 0
                ? $"This backup is older than your current configuration. {Formatting.Count(plugins.Count, "plugin")} configured since ({Formatting.JoinWords(plugins.Take(5))}) will return to {(plugins.Count == 1 ? "its" : "their")} earlier settings."
                : "This backup is older than your current Dalamud settings, which will return to their earlier values.");
        }

        Output.Line();
        var checks = await restores.CheckAsync(record.FilePath, source, cancellationToken);
        foreach (var check in checks)
        {
            Output.Markup(check.Passed
                ? $"[{CliOutput.Healthy}]✓[/] {Markup.Escape(check.Label)} [{CliOutput.Dim}]{Markup.Escape(check.Detail)}[/]"
                : $"[{CliOutput.Critical}]✕[/] {Markup.Escape(check.Label)} [{CliOutput.Dim}]{Markup.Escape(check.Detail)}[/]");
        }

        var failed = checks.FirstOrDefault(check => !check.Passed);
        if (failed is not null)
        {
            Output.Error($"Restore blocked: {failed.Label.ToLowerInvariant()} check failed ({failed.Detail}).");
            return (int)ExitCodeFor(failed.Id);
        }

        Output.Line();
        Output.Detail("XIVault will create a safety backup of your current configuration before restoring anything.");
        if (!settings.Yes)
        {
            if (!Output.Interactive)
            {
                Output.Error("Pass --yes to restore without a confirmation prompt.");
                return (int)XivaultErrorKind.InvalidConfiguration;
            }

            if (!Output.Out.Confirm("Restore this backup?", defaultValue: false))
            {
                Output.Line("Nothing was changed.");
                return 0;
            }
        }

        var result = await Output.WithStatusAsync("Verifying backup integrity", update =>
            restores.RestoreAsync(new RestoreRequest(record.FilePath) { Source = source }, new InlineProgress<RestoreProgress>(progress => update(progress.Message)), cancellationToken));

        Output.Success("Restore complete");
        Output.Detail($"{Formatting.Count(result.PluginConfigCount, "plugin configuration")} restored{(result.DalamudConfigRestored ? " · Dalamud settings restored" : "")}");
        Output.Detail($"Safety snapshot: {result.SafetySnapshot.FileName} in {paths.Friendly(Path.GetDirectoryName(result.SafetySnapshot.FilePath)!)}");
        return 0;
    }

    private void Item(bool included, string label) =>
        Output.Markup(included ? $"  [{CliOutput.Healthy}]✓[/] {Markup.Escape(label)}" : $"  [{CliOutput.Dim}]– {Markup.Escape(label)}[/]");

    private BackupRecord Choose(string? requested)
    {
        var destination = configStore.Load().BackupDestination!;
        if (requested is null)
        {
            var records = catalog.List(destination).Where(record => record.HasManifest).ToList();
            if (records.Count == 0)
            {
                throw new XivaultException(XivaultErrorKind.RestoreValidationFailed, $"There are no backups in {destination}.");
            }

            if (!Output.Interactive)
            {
                throw new XivaultException(XivaultErrorKind.InvalidConfiguration, "Name the backup to restore, for example: xivault restore latest");
            }

            var now = clock.GetLocalNow().DateTime;
            return Output.Out.Prompt(new SelectionPrompt<BackupRecord>()
                .Title("Which backup do you want to restore?")
                .PageSize(10)
                .HighlightStyle(Style.Parse(CliOutput.Accent))
                .UseConverter(record => Markup.Escape(
                    $"{Formatting.DayAndTime(record.CreatedAtUtc.ToLocalTime(), now),-22} {Formatting.Count(record.PluginConfigCount, "plugin config"),-18} {Formatting.Bytes(record.SizeBytes),-9} {Formatting.KindLabel(record.Kind)}"))
                .AddChoices(records));
        }

        if (requested.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            return catalog.List(destination).FirstOrDefault(record => record.HasManifest && !record.IsSafetySnapshot)
                ?? throw new XivaultException(XivaultErrorKind.RestoreValidationFailed, $"There are no backups in {destination}.");
        }

        var candidates = new[]
        {
            requested,
            Path.Combine(destination, requested),
            Path.Combine(destination, requested + BackupNaming.Extension),
        };
        var path = candidates.FirstOrDefault(File.Exists)
            ?? throw new XivaultException(XivaultErrorKind.RestoreValidationFailed, $"No backup named {requested} was found.");
        return catalog.Read(path)
            ?? throw new XivaultException(XivaultErrorKind.RestoreValidationFailed, $"{Path.GetFileName(path)} is not an XIVault backup.");
    }

    private static XivaultErrorKind ExitCodeFor(SafetyCheckId id) => id switch
    {
        SafetyCheckId.XivLauncherClosed or SafetyCheckId.GameClosed => XivaultErrorKind.GameRunning,
        SafetyCheckId.IntegrityVerified => XivaultErrorKind.RestoreValidationFailed,
        SafetyCheckId.DestinationAvailable => XivaultErrorKind.XivLauncherNotFound,
        _ => XivaultErrorKind.DestinationUnavailable,
    };
}

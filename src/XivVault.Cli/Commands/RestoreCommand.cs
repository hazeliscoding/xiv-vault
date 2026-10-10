using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using XivVault.Cli.Infrastructure;
using XivVault.Core;
using XivVault.Core.Backup;
using XivVault.Core.Configuration;
using XivVault.Core.Platform;
using XivVault.Core.Restore;

namespace XivVault.Cli.Commands;

internal sealed class RestoreCommand(
    CliOutput output,
    IConfigStore configStore,
    IBackupCatalog catalog,
    IRestoreService restores,
    PathDisplay paths,
    TimeProvider clock) : XivVaultCommand<RestoreCommand.Settings>(output)
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

        [CommandOption("--plugin <NAME>")]
        [Description("Restore only this plugin's settings. Repeat it to choose more plugins. Other settings stay as they are unless their own option is also given.")]
        public string[] Plugins { get; init; } = [];

        [CommandOption("--dalamud-settings")]
        [Description("Restore Dalamud's own settings: the Dalamud config, the plugin collection database and the UI layout. On its own, nothing else is restored.")]
        public bool DalamudSettings { get; init; }

        [CommandOption("--character-settings")]
        [Description("Restore the game's character settings: each character's HUD layout, hotbars, keybinds, macros and gear sets, with shared macros and appearance saves. On its own, nothing else is restored.")]
        public bool CharacterSettings { get; init; }

        [CommandOption("--system-settings")]
        [Description("Restore the game's system settings (FFXIV.cfg): graphics, sound, display and more. They hold the resolution and monitor, which may not suit another PC.")]
        public bool SystemSettings { get; init; }
    }

    protected override async Task<int> RunAsync(Settings settings, CancellationToken cancellationToken)
    {
        var source = settings.Source is null ? null : Path.GetFullPath(settings.Source);
        var record = Choose(settings.Backup);
        if (record.IsOnlineOnly)
        {
            var size = Formatting.Bytes(record.SizeBytes);
            Output.Line($"This backup is in the cloud, so it downloads first ({size}). Your settings stay as they are until the restore starts.");
            var chosen = record;
            record = await Output.WithStatusAsync($"Downloading {size}", update => Task.Run(
                () => catalog.Download(chosen, new InlineProgress<long>(read => update($"Downloading {Formatting.Bytes(read)} of {size}")), cancellationToken),
                cancellationToken));
        }

        var preview = await restores.PreviewAsync(record.FilePath, source, cancellationToken);
        var selection = settings.Plugins.Length > 0 || settings.DalamudSettings || settings.CharacterSettings || settings.SystemSettings
            ? RestoreSelection.Only(settings.Plugins, settings.DalamudSettings, settings.CharacterSettings, settings.SystemSettings)
            : RestoreSelection.Everything;
        var inBackup = preview.Backup.PluginNames;
        if (preview.ProblemWith(selection) is { } problem)
        {
            if (selection.PluginsMissingFrom(inBackup).Count > 0 && inBackup.Count > 0)
            {
                Output.Detail($"Plugins in this backup: {Formatting.JoinWords(inBackup)}");
            }

            throw new XivVaultException(XivVaultErrorKind.InvalidConfiguration, problem);
        }

        var now = clock.GetLocalNow().DateTime;
        var created = preview.Backup.CreatedAtUtc.ToLocalTime();

        Output.Markup($"[bold]{Markup.Escape(Formatting.FullDate(created))}[/] [{CliOutput.Dim}]· {Markup.Escape(Formatting.KindLabel(preview.Backup.Kind))} · {Markup.Escape(Formatting.Age(created, now))}[/]");
        var contents = preview.Contents;
        var chosenPlugins = inBackup.Where(selection.IncludesPlugin).ToList();
        if (selection.Plugins is null)
        {
            Item(true, Formatting.Count(contents.PluginConfigCount, "plugin configuration"));
        }
        else
        {
            Item(chosenPlugins.Count > 0, chosenPlugins.Count > 0
                ? $"{chosenPlugins.Count} of {Formatting.Count(contents.PluginConfigCount, "plugin configuration")}: {Formatting.JoinWords(chosenPlugins)}"
                : "Plugin configurations — not chosen");
        }

        if (selection.DalamudSettings)
        {
            Item(contents.DalamudConfig, "Dalamud settings");
            Item(contents.DalamudVfs, "Plugin collection database");
            Item(contents.DalamudConfig, contents.CustomRepositoryCount is { } repos and > 0
                ? $"Custom repository settings · {Formatting.Count(repos, "repo")}"
                : "Custom repository settings");
            Item(contents.DalamudUi, contents.DalamudUi ? "UI layout" : "UI layout — not included");
        }
        else
        {
            Item(false, "Dalamud settings, plugin collection database and UI layout — not chosen");
        }

        if (!selection.CharacterSettings)
        {
            Item(false, "Character settings — not chosen");
        }
        else
        {
            Item(contents.CharacterSettings, contents.CharacterSettings
                ? $"Character settings · {Formatting.Count(contents.CharacterCount, "character")}"
                : "Character settings — not in this backup");
        }

        if (!selection.SystemSettings)
        {
            Item(false, "System settings — not chosen");
        }
        else
        {
            Item(contents.SystemSettings, contents.SystemSettings ? "System settings · FFXIV.cfg" : "System settings — not in this backup");
        }

        var undone = preview.ChangesUndoneBy(selection);
        if (undone.Any)
        {
            var plugins = undone.Plugins;
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
        Output.Detail("XIV Vault will create a safety backup of your current configuration before restoring anything.");
        if (!settings.Yes)
        {
            if (!Output.Interactive)
            {
                Output.Error("Pass --yes to restore without a confirmation prompt.");
                return (int)XivVaultErrorKind.InvalidConfiguration;
            }

            if (!Output.Out.Confirm("Restore this backup?", defaultValue: false))
            {
                Output.Line("Nothing was changed.");
                return 0;
            }
        }

        var result = await Output.WithStatusAsync("Verifying backup integrity", update =>
            restores.RestoreAsync(
                new RestoreRequest(record.FilePath) { Source = source, Selection = selection },
                new InlineProgress<RestoreProgress>(progress => update(progress.Message)),
                cancellationToken));

        Output.Success("Restore complete");
        var restoredPlugins = result.PluginConfigCount > 0 || selection.Plugins is null
            ? $"{Formatting.Count(result.PluginConfigCount, "plugin configuration")} restored"
            : "Plugin configurations unchanged";
        var restoredSettings = (result.DalamudConfigRestored ? " · Dalamud settings restored" : "")
            + (result.CharacterSettingsRestored ? " · character settings restored" : "")
            + (result.SystemSettingsRestored ? " · system settings restored" : "");
        Output.Detail(restoredPlugins + restoredSettings);
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
            var records = catalog.List(destination).Where(record => record.IsRecognized).ToList();
            if (records.Count == 0)
            {
                throw new XivVaultException(XivVaultErrorKind.RestoreValidationFailed, $"There are no backups in {destination}.");
            }

            if (!Output.Interactive)
            {
                throw new XivVaultException(XivVaultErrorKind.InvalidConfiguration, "Name the backup to restore, for example: xiv-vault restore latest");
            }

            var now = clock.GetLocalNow().DateTime;
            return Output.Out.Prompt(new SelectionPrompt<BackupRecord>()
                .Title("Which backup do you want to restore?")
                .PageSize(10)
                .HighlightStyle(Style.Parse(CliOutput.Accent))
                .UseConverter(record => Markup.Escape(
                    $"{Formatting.DayAndTime(record.CreatedAtUtc.ToLocalTime(), now),-22} {(record.HasManifest ? Formatting.Count(record.PluginConfigCount, "plugin config") : "in the cloud"),-18} {Formatting.Bytes(record.SizeBytes),-9} {Formatting.KindLabel(record)}"))
                .AddChoices(records));
        }

        if (requested.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            return catalog.List(destination).FirstOrDefault(record => record.IsRegular)
                ?? throw new XivVaultException(XivVaultErrorKind.RestoreValidationFailed, $"There are no backups in {destination}.");
        }

        var candidates = new[]
        {
            requested,
            Path.Combine(destination, requested),
            Path.Combine(destination, requested + BackupNaming.Extension),
        };
        var path = candidates.FirstOrDefault(File.Exists)
            ?? throw new XivVaultException(XivVaultErrorKind.RestoreValidationFailed, $"No backup named {requested} was found.");
        return catalog.Read(path)
            ?? throw new XivVaultException(XivVaultErrorKind.RestoreValidationFailed, $"{Path.GetFileName(path)} is not a XIV Vault backup.");
    }

    private static XivVaultErrorKind ExitCodeFor(SafetyCheckId id) => id switch
    {
        SafetyCheckId.XivLauncherClosed or SafetyCheckId.GameClosed => XivVaultErrorKind.GameRunning,
        SafetyCheckId.IntegrityVerified => XivVaultErrorKind.RestoreValidationFailed,
        SafetyCheckId.DestinationAvailable => XivVaultErrorKind.XivLauncherNotFound,
        _ => XivVaultErrorKind.DestinationUnavailable,
    };
}

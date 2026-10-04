using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using XIVault.Cli.Infrastructure;
using XIVault.Core;
using XIVault.Core.Platform;
using XIVault.Core.Status;

namespace XIVault.Cli.Commands;

internal sealed class StatusCommand(
    CliOutput output,
    IStatusService statusService,
    PathDisplay paths,
    TimeProvider clock) : XivaultCommand<StatusCommand.Settings>(output)
{
    internal sealed class Settings : GlobalSettings
    {
        [CommandOption("--json")]
        [Description("Print the status as JSON.")]
        public bool Json { get; init; }
    }

    protected override async Task<int> RunAsync(Settings settings, CancellationToken cancellationToken)
    {
        var status = await statusService.GetAsync(verifyLatest: true, cancellationToken);
        if (settings.Json)
        {
            Output.Json(JsonModels.From(status));
            return 0;
        }

        var now = clock.GetLocalNow().DateTime;
        var grid = new Grid().AddColumn(new GridColumn().NoWrap().PadRight(3)).AddColumn();
        grid.AddRow($"[{CliOutput.Dim}]Backup health[/]", State(status));

        var latest = status.LatestBackup;
        grid.AddRow($"[{CliOutput.Dim}]Last backup[/]", latest is null
            ? $"[{CliOutput.Dim}]none yet[/]"
            : Markup.Escape($"{Formatting.DayAndTime(latest.CreatedAtUtc.ToLocalTime(), now)} · {Formatting.Bytes(latest.SizeBytes)} · {Formatting.IntegrityLabel(latest.Integrity).ToLowerInvariant()}"));
        var pluginCount = latest?.PluginConfigCount ?? status.Portable?.PluginConfigCount;
        if (pluginCount is { } count)
        {
            grid.AddRow($"[{CliOutput.Dim}]Plugin configs[/]", count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        grid.AddRow($"[{CliOutput.Dim}]Destination[/]", Markup.Escape(paths.Friendly(status.Destination))
            + (status.DestinationAvailable ? "" : $" [{CliOutput.Warn}](not created yet or unavailable)[/]"));
        grid.AddRow($"[{CliOutput.Dim}]Backups[/]", Markup.Escape($"{Formatting.Count(status.Backups.Count, "backup")} · {Formatting.Bytes(status.TotalBytes)} · keeping latest {status.Config.RetentionCount}"));

        var schedule = status.Schedule;
        grid.AddRow($"[{CliOutput.Dim}]Automatic[/]", status.AutomaticBackupsEnabled && schedule.InstalledSettings is { } installed
            ? Markup.Escape(Formatting.Schedule(installed) + (schedule.NextRunLocal is { } next ? $" · next {Formatting.DayAndTime(next, now)}" : ""))
            : $"[{CliOutput.Dim}]off[/]");

        var installation = status.Launcher.Installation;
        grid.AddRow($"[{CliOutput.Dim}]XIVLauncher[/]", installation is null
            ? $"[{CliOutput.Critical}]not found[/]"
            : Markup.Escape(paths.Friendly(installation.RootPath) + (installation.LauncherVersion is { } version ? $" · v{version}" : "")));

        Output.Markup($"[bold]XIV Vault[/] [{CliOutput.Dim}]{XivaultInfo.Version}[/]");
        Output.Write(grid);
        return 0;
    }

    private static string State(XivaultStatus status) => status.State switch
    {
        ProtectionState.Protected => $"[{CliOutput.Healthy}]● Protected[/]",
        ProtectionState.NotBackedUp => $"[{CliOutput.Warn}]● Not backed up yet[/]",
        ProtectionState.NoConfiguration => $"[{CliOutput.Warn}]● No Dalamud configuration yet[/]",
        ProtectionState.NotDetected => $"[{CliOutput.Critical}]● XIVLauncher not found[/]",
        _ => $"[{CliOutput.Warn}]● Needs attention[/] [{CliOutput.Dim}]{Markup.Escape(status.AttentionReason ?? "")}[/]",
    };
}

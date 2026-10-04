using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using XivVault.Cli.Infrastructure;
using XivVault.Core;
using XivVault.Core.Diagnostics;

namespace XivVault.Cli.Commands;

internal sealed class DoctorCommand(CliOutput output, IDiagnosticsService diagnostics) : XivVaultCommand<DoctorCommand.Settings>(output)
{
    internal sealed class Settings : GlobalSettings
    {
        [CommandOption("--json")]
        [Description("Print the results as JSON.")]
        public bool Json { get; init; }

        [CommandOption("--report")]
        [Description("Print the plain-text report to paste into a GitHub issue.")]
        public bool Report { get; init; }
    }

    protected override async Task<int> RunAsync(Settings settings, CancellationToken cancellationToken)
    {
        var report = await diagnostics.RunAsync(cancellationToken);
        if (settings.Json)
        {
            Output.Json(JsonModels.From(report));
        }
        else if (settings.Report)
        {
            Output.Out.Profile.Out.Writer.Write(report.ToText());
        }
        else
        {
            foreach (var group in report.Groups)
            {
                Output.Markup($"[bold]{Markup.Escape(group.Title)}[/] {Badge(group.Status)}");
                foreach (var check in group.Checks)
                {
                    Output.Markup($"  {Marker(check.Status)} {Markup.Escape(check.Label)} [{CliOutput.Dim}]{Markup.Escape(check.Detail)}[/]");
                }

                Output.Line();
            }

            Output.Line(report.Summary);
            Output.Detail("Run xiv-vault doctor --report for a copy you can share. It holds paths, versions and results only.");
        }

        return ExitCode(report);
    }

    /// <summary>Zero unless a check failed; then the exit code of the first failing area.</summary>
    private static int ExitCode(DiagnosticReport report)
    {
        var failing = report.Groups.FirstOrDefault(group => group.Status == DiagnosticStatus.Error);
        return failing?.Area switch
        {
            null => 0,
            DiagnosticArea.XivLauncher or DiagnosticArea.Dalamud => (int)XivVaultErrorKind.XivLauncherNotFound,
            DiagnosticArea.BackupDestination => (int)XivVaultErrorKind.DestinationUnavailable,
            _ => (int)XivVaultErrorKind.Unexpected,
        };
    }

    private static string Badge(DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Healthy => $"[{CliOutput.Healthy}]Healthy[/]",
        DiagnosticStatus.Warning => $"[{CliOutput.Warn}]Suggestion[/]",
        _ => $"[{CliOutput.Critical}]Problem[/]",
    };

    private static string Marker(DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Healthy => $"[{CliOutput.Healthy}]✓[/]",
        DiagnosticStatus.Warning => $"[{CliOutput.Warn}]![/]",
        _ => $"[{CliOutput.Critical}]✕[/]",
    };
}

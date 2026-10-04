using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;
using XIVault.Cli.Commands;
using XIVault.Cli.Infrastructure;
using XIVault.Core;
using XIVault.Core.Logging;
using XIVault.Core.Platform;

namespace XIVault.Cli;

public static class XivaultCli
{
    public static Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default) =>
        RunAsync(args, configure: null, console: null, cancellationToken);

    /// <summary>Entry point that tests use to swap in a fake profile and capture the output.</summary>
    internal static async Task<int> RunAsync(string[] args, Action<IServiceCollection>? configure, IAnsiConsole? console, CancellationToken cancellationToken = default)
    {
        var output = console ?? AnsiConsole.Console;
        var error = console ?? AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });
        var verbose = args.Any(arg => arg is "-v" or "--verbose");

        var services = new ServiceCollection();
        services.AddSingleton(new CliOutput(output, error));
        configure?.Invoke(services);
        services.AddXivaultCore();
        services.AddSingleton<ILoggerProvider>(provider =>
            new FileLoggerProvider(Path.Combine(provider.GetRequiredService<IAppEnvironment>().DataDirectory, "logs")));
        if (verbose)
        {
            services.AddSingleton<ILoggerProvider>(new ConsoleLoggerProvider(error));
        }

        services.AddLogging(logging => logging.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information));

        var app = new CommandApp(new TypeRegistrar(services));
        app.Configure(config =>
        {
            config.SetApplicationName("xivault");
            config.SetApplicationVersion(XivaultInfo.Version);
            config.ConfigureConsole(output);
            config.CaseSensitivity(CaseSensitivity.None);
            config.UseStrictParsing();
            config.Settings.CancellationExitCode = (int)XivaultErrorKind.Unexpected;
            config.SetExceptionHandler((exception, _) =>
            {
                // Parse and validation problems are the caller's to fix; everything else is unexpected.
                if (exception is CommandParseException or CommandRuntimeException)
                {
                    error.MarkupLine($"[{CliOutput.Critical}]✕[/] {Markup.Escape(exception.Message)}");
                    return (int)XivaultErrorKind.InvalidConfiguration;
                }

                error.MarkupLine($"[{CliOutput.Critical}]✕[/] Unexpected error: {Markup.Escape(exception.Message)}");
                return (int)XivaultErrorKind.Unexpected;
            });

            config.AddCommand<BackupCommand>("backup")
                .WithDescription("Back up the portable XIVLauncher / Dalamud configuration.")
                .WithExample("backup")
                .WithExample("backup", "--quiet")
                .WithExample("backup", "--destination", @"D:\XIVault");
            config.AddCommand<RestoreCommand>("restore")
                .WithDescription("Restore a backup. A safety backup of the current configuration is always made first.")
                .WithExample("restore")
                .WithExample("restore", "latest");
            config.AddCommand<ListCommand>("list")
                .WithDescription("List the backups in the backup folder.");
            config.AddCommand<StatusCommand>("status")
                .WithDescription("Show whether your Dalamud setup is protected.")
                .WithExample("status", "--json");
            config.AddCommand<DoctorCommand>("doctor")
                .WithDescription("Check XIVLauncher, Dalamud, the backup folder and scheduling.")
                .WithExample("doctor", "--report");
            config.AddBranch("schedule", schedule =>
            {
                schedule.SetDescription("Turn automatic backups on or off (Windows Task Scheduler).");
                schedule.AddCommand<ScheduleDailyCommand>("daily").WithDescription("Back up every day.").WithExample("schedule", "daily", "--time", "21:00");
                schedule.AddCommand<ScheduleWeeklyCommand>("weekly").WithDescription("Back up on chosen days.").WithExample("schedule", "weekly", "--day", "Sunday");
                schedule.AddCommand<ScheduleLogonCommand>("logon").WithDescription("Back up each time you sign in to Windows.");
                schedule.AddCommand<ScheduleStatusCommand>("status").WithDescription("Show the schedule, next run and last result.");
                schedule.AddCommand<ScheduleRemoveCommand>("remove").WithDescription("Turn automatic backups off.");
            });
            config.AddBranch("config", settings =>
            {
                settings.SetDescription("Show or change XIVault's settings.");
                settings.SetDefaultCommand<ConfigShowCommand>();
                settings.AddCommand<ConfigShowCommand>("show").WithDescription("Show the current settings.");
                settings.AddCommand<ConfigSetCommand>("set").WithDescription("Change a setting.").WithExample("config", "set", "retention", "15");
                settings.AddCommand<ConfigPathCommand>("path").WithDescription("Print where the settings file is.");
            });
            config.AddCommand<VersionCommand>("version").WithDescription("Print the XIVault version.");
        });

        return await app.RunAsync(args, cancellationToken).ConfigureAwait(false);
    }
}

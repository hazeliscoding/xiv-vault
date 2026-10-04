using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using XivVault.Cli.Infrastructure;
using XivVault.Core;
using XivVault.Core.Configuration;
using XivVault.Core.Platform;

namespace XivVault.Cli.Commands;

internal sealed class ConfigShowCommand(CliOutput output, IConfigStore configStore, PathDisplay paths)
    : XivVaultCommand<ConfigShowCommand.Settings>(output)
{
    internal sealed class Settings : GlobalSettings
    {
        [CommandOption("--json")]
        [Description("Print the settings as JSON.")]
        public bool Json { get; init; }
    }

    protected override Task<int> RunAsync(Settings settings, CancellationToken cancellationToken)
    {
        var config = configStore.Load();
        if (settings.Json)
        {
            Output.Json(config);
            return Task.FromResult(0);
        }

        var grid = new Grid().AddColumn(new GridColumn().NoWrap().PadRight(3)).AddColumn();
        var friendly = paths.Friendly(config.BackupDestination!);
        grid.AddRow($"[{CliOutput.Dim}]destination[/]", Markup.Escape(config.BackupDestination!) + (friendly == config.BackupDestination ? "" : $" [{CliOutput.Dim}]({Markup.Escape(friendly)})[/]"));
        grid.AddRow($"[{CliOutput.Dim}]retention[/]", $"keep latest {config.RetentionCount}");
        grid.AddRow($"[{CliOutput.Dim}]include-ui[/]", config.IncludeDalamudUi ? "yes" : "no");
        grid.AddRow($"[{CliOutput.Dim}]compression[/]", config.Compression.ToString().ToLowerInvariant());
        grid.AddRow($"[{CliOutput.Dim}]source[/]", config.XivLauncherPathOverride is { } source ? Markup.Escape(source) : "auto (detected)");
        grid.AddRow($"[{CliOutput.Dim}]schedule[/]", Markup.Escape(config.Schedule.Enabled ? Formatting.Schedule(config.Schedule) : "off"));
        Output.Write(grid);
        Output.Detail($"Settings file: {configStore.ConfigPath}");
        return Task.FromResult(0);
    }
}

internal sealed class ConfigSetCommand(CliOutput output, IConfigStore configStore) : XivVaultCommand<ConfigSetCommand.Settings>(output)
{
    internal sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<KEY>")]
        [Description("destination, retention, include-ui, compression or source.")]
        public string Key { get; init; } = "";

        [CommandArgument(1, "<VALUE>")]
        [Description("The new value. For source, \"auto\" goes back to detection.")]
        public string Value { get; init; } = "";
    }

    protected override Task<int> RunAsync(Settings settings, CancellationToken cancellationToken)
    {
        var config = configStore.Load();
        var value = settings.Value.Trim();
        config = settings.Key.ToLowerInvariant() switch
        {
            "destination" => config with { BackupDestination = Path.GetFullPath(value) },
            "retention" => int.TryParse(value, out var count)
                ? config with { RetentionCount = count }
                : throw Invalid("retention must be a number."),
            "include-ui" => bool.TryParse(value, out var include) || TryYesNo(value, out include)
                ? config with { IncludeDalamudUi = include }
                : throw Invalid("include-ui must be true or false."),
            "compression" => Enum.TryParse<CompressionPreset>(value, ignoreCase: true, out var preset) && Enum.IsDefined(preset)
                ? config with { Compression = preset }
                : throw Invalid("compression must be fast, balanced or maximum."),
            "source" => config with { XivLauncherPathOverride = value.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : Path.GetFullPath(value) },
            _ => throw Invalid($"Unknown setting '{settings.Key}'. Use destination, retention, include-ui, compression or source."),
        };

        configStore.Save(config);
        Output.Success($"Saved {settings.Key.ToLowerInvariant()}.");
        return Task.FromResult(0);
    }

    private static bool TryYesNo(string value, out bool result)
    {
        result = value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value.Equals("on", StringComparison.OrdinalIgnoreCase);
        return result || value.Equals("no", StringComparison.OrdinalIgnoreCase) || value.Equals("off", StringComparison.OrdinalIgnoreCase);
    }

    private static XivVaultException Invalid(string message) => new(XivVaultErrorKind.InvalidConfiguration, message);
}

internal sealed class ConfigPathCommand(CliOutput output, IConfigStore configStore) : XivVaultCommand<GlobalSettings>(output)
{
    protected override Task<int> RunAsync(GlobalSettings settings, CancellationToken cancellationToken)
    {
        Output.Out.Profile.Out.Writer.WriteLine(configStore.ConfigPath);
        return Task.FromResult(0);
    }
}

internal sealed class VersionCommand(CliOutput output) : XivVaultCommand<GlobalSettings>(output)
{
    protected override Task<int> RunAsync(GlobalSettings settings, CancellationToken cancellationToken)
    {
        Output.Out.Profile.Out.Writer.WriteLine($"XIV Vault {XivVaultInfo.Version}");
        if (settings.Verbose)
        {
            Output.Detail($".NET {Environment.Version} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        }

        return Task.FromResult(0);
    }
}

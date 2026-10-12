using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Testing;
using XivVault.Cli;
using XivVault.Core;
using XivVault.Core.Platform;
using XivVault.Core.State;
using XivVault.Tests.Support;

namespace XivVault.Tests.Cli;

/// <summary>
/// The 1.0 promise in docs/cli.md: JSON field names and exit codes stay through 1.x. A change that
/// breaks one of these tests breaks scripts; add fields and codes, never rename or remove them.
/// </summary>
public class CliContractTests
{
    private static async Task<(int Code, string Output)> Run(TestHost host, params string[] args)
    {
        var console = new TestConsole();
        console.Profile.Width = 200;
        var code = await XivVaultCli.RunAsync(
            args,
            services =>
            {
                services.AddSingleton<IAppEnvironment>(host.Environment);
                services.AddSingleton<IProcessInspector>(host.Processes);
                services.AddSingleton<ICommandRunner>(host.Commands);
                services.AddSingleton<TimeProvider>(host.Clock);
                services.AddSingleton<IFileAvailability>(host.Files);
                services.AddSingleton<INotifier>(host.Notifications);
                services.AddSingleton<IWebDownloader>(host.Downloads);
            },
            console,
            TestContext.Current.CancellationToken);
        return (code, console.Output);
    }

    /// <summary>A setup with everything filled in, so no object in the output is null.</summary>
    private static async Task<TestHost> FullSetup()
    {
        var host = new TestHost();
        host.CreateLauncher();
        host.CreateGameSettings();
        string? task = null;
        host.Commands.Handler = (_, args) =>
        {
            if (args[0] == "/Create")
            {
                task = File.ReadAllText(args[4]);
                return new(0, "", "");
            }

            return task is null ? new(1, "", "not found") : new(0, task, "");
        };
        await Run(host, "schedule", "weekly", "--day", "Sunday", "--time", "18:30");
        await host.BackUpAsync();
        host.State.Update(state => state with { LastScheduledRun = new ScheduledRunRecord(host.Clock.GetUtcNow().UtcDateTime, ScheduledRunResult.Success, "Successful", "xiv-vault-2026-10-04-131900.zip") });
        return host;
    }

    private static readonly string[] BackupFields =
        ["file", "path", "createdAtUtc", "kind", "sizeBytes", "pluginConfigCount", "integrity", "onlineOnly", "problem", "characterCount", "systemSettings"];

    private static readonly string[] ScheduleFields =
        ["installed", "frequency", "days", "time", "nextRunLocal", "lastRun", "lastRun.atUtc", "lastRun.result", "lastRun.message", "lastRun.backupFile"];

    public static TheoryData<string[], string[]> Shapes => new()
    {
        {
            ["status", "--json"],
            [
                "version", "state", "attentionReason",
                "xivLauncher", "xivLauncher.found", "xivLauncher.path", "xivLauncher.layout", "xivLauncher.version", "xivLauncher.hasConfiguration", "xivLauncher.message",
                "pluginConfigCount", "destination", "destinationAvailable",
                "latestBackup", .. BackupFields.Select(name => "latestBackup." + name),
                "backupCount", "totalBytes", "retentionCount",
                "schedule", .. ScheduleFields.Select(name => "schedule." + name),
                "gameSettings", "gameSettings.found", "gameSettings.included", "gameSettings.characterCount", "gameSettings.systemSettings",
            ]
        },
        { ["list", "--json"], [.. BackupFields.Select(name => "[]." + name)] },
        { ["schedule", "status", "--json"], ScheduleFields },
        {
            ["doctor", "--json"],
            ["version", "generatedAtUtc", "summary", "status", "groups", "groups[].area", "groups[].title", "groups[].status", "groups[].checks", "groups[].checks[].label", "groups[].checks[].status", "groups[].checks[].detail"]
        },
        {
            ["config", "show", "--json"],
            ["backupDestination", "retentionCount", "includeDalamudUi", "includeGameSettings", "compression", "xivLauncherPathOverride", "schedule", "schedule.enabled", "schedule.frequency", "schedule.days", "schedule.time", "checkForUpdates"]
        },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task Json_output_keeps_its_fields(string[] command, string[] fields)
    {
        using var host = await FullSetup();

        var (code, output) = await Run(host, command);

        Assert.Equal(0, code);
        Assert.Equal(fields.Order(), Paths(JsonDocument.Parse(output).RootElement).Distinct().Order());
    }

    [Fact]
    public async Task Schedule_json_uses_the_same_words_as_config_json()
    {
        using var host = await FullSetup();

        var weekly = JsonDocument.Parse((await Run(host, "schedule", "status", "--json")).Output).RootElement;
        await Run(host, "schedule", "logon");
        var logon = JsonDocument.Parse((await Run(host, "schedule", "status", "--json")).Output).RootElement;
        var config = JsonDocument.Parse((await Run(host, "config", "show", "--json")).Output).RootElement.GetProperty("schedule");

        Assert.Equal("weekly", weekly.GetProperty("frequency").GetString());
        Assert.Equal("sunday", weekly.GetProperty("days")[0].GetString());
        Assert.Equal("logon", logon.GetProperty("frequency").GetString());
        Assert.Equal("logon", config.GetProperty("frequency").GetString());
        Assert.Equal("sunday", config.GetProperty("days")[0].GetString());
    }

    [Fact]
    public void Exit_codes_keep_their_numbers() =>
        Assert.Equal(
            ["Unexpected=1", "InvalidConfiguration=2", "XivLauncherNotFound=3", "BackupValidationFailed=4", "RestoreValidationFailed=5", "GameRunning=6", "DestinationUnavailable=7"],
            Enum.GetValues<XivVaultErrorKind>().Select(kind => $"{kind}={(int)kind}"));

    private static IEnumerable<string> Paths(JsonElement element, string path = "")
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var child = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
                yield return child;
                foreach (var nested in Paths(property.Value, child))
                {
                    yield return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() > 0)
        {
            foreach (var nested in Paths(element[0], path + "[]"))
            {
                yield return nested;
            }
        }
    }
}

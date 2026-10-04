using System.ComponentModel;
using Spectre.Console.Cli;
using XivVault.Core;

namespace XivVault.Cli.Infrastructure;

internal class GlobalSettings : CommandSettings
{
    [CommandOption("-v|--verbose")]
    [Description("Show what XIV Vault is doing, step by step.")]
    public bool Verbose { get; init; }
}

/// <summary>Turns Core failures into a readable message and the documented exit code.</summary>
internal abstract class XivVaultCommand<TSettings>(CliOutput output) : AsyncCommand<TSettings>
    where TSettings : GlobalSettings
{
    protected CliOutput Output { get; } = output;

    public sealed override async Task<int> ExecuteAsync(CommandContext context, TSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            return await RunAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (XivVaultException ex)
        {
            Output.Error(ex.Message);
            return (int)ex.Kind;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Output.Error($"A file operation failed: {ex.Message}");
            return (int)XivVaultErrorKind.Unexpected;
        }
        catch (OperationCanceledException)
        {
            Output.Error("Cancelled.");
            return (int)XivVaultErrorKind.Unexpected;
        }
    }

    protected abstract Task<int> RunAsync(TSettings settings, CancellationToken cancellationToken);
}

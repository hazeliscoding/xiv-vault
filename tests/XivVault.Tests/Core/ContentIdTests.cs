using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XivVault.Core;
using XivVault.Core.Backup;
using XivVault.Core.Configuration;
using XivVault.Core.Discovery;
using XivVault.Core.Logging;
using XivVault.Core.Platform;
using XivVault.Core.Restore;
using XivVault.Tests.Support;

namespace XivVault.Tests.Core;

/// <summary>
/// A character folder is named by a content ID that identifies the character, so nothing XIV Vault
/// shows or logs may contain one.
/// </summary>
public class ContentIdTests
{
    private const string ContentId = "004000174A1B2C3D";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Log_files_never_show_a_content_ID()
    {
        using var env = new TestEnvironment();
        var folder = Path.Combine(env.Root, "logs");
        var provider = new FileLoggerProvider(folder);
        var logger = provider.CreateLogger("Test");
        var path = Path.Combine(env.Documents, "My Games", "FINAL FANTASY XIV - A Realm Reborn", "FFXIV_CHR" + ContentId, "HOTBAR.DAT");

        logger.LogError(new IOException($"Access to the path '{path}' is denied."), "Could not restore {Path}", path);

        var text = File.ReadAllText(provider.CurrentFile);
        Assert.Contains("HOTBAR.DAT", text, StringComparison.Ordinal);
        Assert.DoesNotContain(ContentId, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_failed_restore_explains_itself_without_a_content_ID()
    {
        using var host = new TestHost(configure: services => services.AddSingleton<IRestoreService>(provider => new RestoreService(
            provider.GetRequiredService<IConfigStore>(),
            provider.GetRequiredService<IXivLauncherLocator>(),
            provider.GetRequiredService<ArchiveValidator>(),
            provider.GetRequiredService<BackupCatalog>(),
            provider.GetRequiredService<BackupService>(),
            provider.GetRequiredService<PortableStateScanner>(),
            provider.GetRequiredService<GameProcessGuard>(),
            provider.GetRequiredService<PathDisplay>(),
            provider.GetRequiredService<IAppEnvironment>(),
            provider.GetRequiredService<RetentionService>(),
            provider.GetRequiredService<OperationLock>(),
            NullLogger<RestoreService>.Instance,
            new DeniedInCharacterFolders())));
        host.CreateLauncher();
        host.CreateGameSettings();
        var backup = await host.BackUpAsync();

        var error = await Assert.ThrowsAsync<XivVaultException>(
            () => host.Restores.RestoreAsync(new RestoreRequest(backup.Record.FilePath), cancellationToken: Ct));

        Assert.Contains("HOTBAR.DAT", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ContentId, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Fails the way Windows does, with the full path in the message.</summary>
    private sealed class DeniedInCharacterFolders : IRestoreFileWriter
    {
        private readonly RenameRestoreFileWriter _inner = new();

        public void Replace(string stagedPath, string targetPath)
        {
            if (targetPath.EndsWith("HOTBAR.DAT", StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException($"Access to the path '{targetPath}' is denied.");
            }

            _inner.Replace(stagedPath, targetPath);
        }
    }
}

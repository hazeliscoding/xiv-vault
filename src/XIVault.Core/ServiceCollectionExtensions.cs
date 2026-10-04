using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XIVault.Core.Backup;
using XIVault.Core.Configuration;
using XIVault.Core.Diagnostics;
using XIVault.Core.Discovery;
using XIVault.Core.Platform;
using XIVault.Core.Restore;
using XIVault.Core.Scheduling;
using XIVault.Core.State;
using XIVault.Core.Status;

namespace XIVault.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the engine both front ends share. Platform services are added with TryAdd, so
    /// tests and front ends can register their own first.
    /// </summary>
    public static IServiceCollection AddXivaultCore(this IServiceCollection services)
    {
        services.AddLogging();
        services.TryAddSingleton<IAppEnvironment, SystemAppEnvironment>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IProcessInspector, SystemProcessInspector>();
        services.TryAddSingleton<ICommandRunner, ProcessCommandRunner>();

        services.TryAddSingleton<IConfigStore, ConfigStore>();
        services.TryAddSingleton<IStateStore, StateStore>();
        services.TryAddSingleton<PathDisplay>();
        services.TryAddSingleton<IXivLauncherLocator, WindowsXivLauncherLocator>();
        services.TryAddSingleton<PortableStateScanner>();
        services.TryAddSingleton<ArchiveValidator>();
        services.TryAddSingleton<BackupCatalog>();
        services.TryAddSingleton<IBackupCatalog>(provider => provider.GetRequiredService<BackupCatalog>());
        services.TryAddSingleton<RetentionService>();
        services.TryAddSingleton<BackupService>();
        services.TryAddSingleton<IBackupService>(provider => provider.GetRequiredService<BackupService>());
        services.TryAddSingleton<GameProcessGuard>();
        services.TryAddSingleton<IRestoreService, RestoreService>();
        services.TryAddSingleton<IBackupScheduler, WindowsTaskScheduler>();
        services.TryAddSingleton<ScheduleService>();
        services.TryAddSingleton<ScheduledBackupRunner>();
        services.TryAddSingleton<IStatusService, StatusService>();
        services.TryAddSingleton<IDiagnosticsService, DiagnosticsService>();
        return services;
    }
}

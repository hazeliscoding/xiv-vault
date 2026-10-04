using System.Globalization;
using System.Text.Json;
using XivVault.Core.Platform;
using XivVault.Core.Serialization;

namespace XivVault.Core.Configuration;

public interface IConfigStore
{
    string ConfigPath { get; }

    /// <summary>Loads the config, filling in the default destination. A missing file gives the defaults.</summary>
    XivVaultConfig Load();

    void Save(XivVaultConfig config);

    string DefaultDestination { get; }
}

public sealed class ConfigStore(IAppEnvironment environment) : IConfigStore
{
    public string ConfigPath => Path.Combine(environment.DataDirectory, "config.json");

    public string DefaultDestination => environment.OneDrive is { } oneDrive
        ? Path.Combine(oneDrive, "XIV Vault")
        : Path.Combine(environment.Documents, "XIV Vault");

    public XivVaultConfig Load()
    {
        XivVaultConfig config;
        if (!File.Exists(ConfigPath))
        {
            config = new XivVaultConfig();
        }
        else
        {
            try
            {
                var json = AtomicFile.ReadAllText(ConfigPath);
                config = JsonSerializer.Deserialize(json, XivVaultJsonContext.Default.XivVaultConfig)
                    ?? throw new JsonException("The file is empty.");
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw new XivVaultException(
                    XivVaultErrorKind.InvalidConfiguration,
                    $"The settings file {ConfigPath} is not valid JSON ({ex.Message}). Fix it, or delete it to start from the defaults.",
                    ex);
            }
        }

        Validate(config);
        return string.IsNullOrWhiteSpace(config.BackupDestination)
            ? config with { BackupDestination = DefaultDestination }
            : config;
    }

    public void Save(XivVaultConfig config)
    {
        Validate(config);
        var json = JsonSerializer.Serialize(config, XivVaultJsonContext.Default.XivVaultConfig);
        AtomicFile.WriteAllText(ConfigPath, json);
    }

    public static void Validate(XivVaultConfig config)
    {
        if (config.RetentionCount is < XivVaultConfig.MinRetentionCount or > XivVaultConfig.MaxRetentionCount)
        {
            throw new XivVaultException(
                XivVaultErrorKind.InvalidConfiguration,
                $"retentionCount must be between {XivVaultConfig.MinRetentionCount} and {XivVaultConfig.MaxRetentionCount}.");
        }

        if (!TimeOnly.TryParseExact(config.Schedule.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw new XivVaultException(XivVaultErrorKind.InvalidConfiguration, "schedule.time must be a 24-hour time such as 18:30.");
        }

        if (config.Schedule.Frequency == ScheduleFrequency.Weekly && config.Schedule.Days.Count == 0)
        {
            throw new XivVaultException(XivVaultErrorKind.InvalidConfiguration, "A weekly schedule needs at least one day.");
        }

        if (config.BackupDestination is { Length: > 0 } destination && !Path.IsPathFullyQualified(destination))
        {
            throw new XivVaultException(XivVaultErrorKind.InvalidConfiguration, "backupDestination must be a full path, such as D:\\XIV Vault.");
        }

        if (config.XivLauncherPathOverride is { Length: > 0 } source && !Path.IsPathFullyQualified(source))
        {
            throw new XivVaultException(XivVaultErrorKind.InvalidConfiguration, "xivLauncherPathOverride must be a full path.");
        }
    }
}

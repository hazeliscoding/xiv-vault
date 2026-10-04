using System.Globalization;
using System.Text.Json;
using XIVault.Core.Platform;
using XIVault.Core.Serialization;

namespace XIVault.Core.Configuration;

public interface IConfigStore
{
    string ConfigPath { get; }

    /// <summary>Loads the config, filling in the default destination. A missing file gives the defaults.</summary>
    XivaultConfig Load();

    void Save(XivaultConfig config);

    string DefaultDestination { get; }
}

public sealed class ConfigStore(IAppEnvironment environment) : IConfigStore
{
    public string ConfigPath => Path.Combine(environment.DataDirectory, "config.json");

    public string DefaultDestination => environment.OneDrive is { } oneDrive
        ? Path.Combine(oneDrive, "XIVault")
        : Path.Combine(environment.Documents, "XIVault");

    public XivaultConfig Load()
    {
        XivaultConfig config;
        if (!File.Exists(ConfigPath))
        {
            config = new XivaultConfig();
        }
        else
        {
            try
            {
                var json = AtomicFile.ReadAllText(ConfigPath);
                config = JsonSerializer.Deserialize(json, XivaultJsonContext.Default.XivaultConfig)
                    ?? throw new JsonException("The file is empty.");
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw new XivaultException(
                    XivaultErrorKind.InvalidConfiguration,
                    $"The settings file {ConfigPath} is not valid JSON ({ex.Message}). Fix it, or delete it to start from the defaults.",
                    ex);
            }
        }

        Validate(config);
        return string.IsNullOrWhiteSpace(config.BackupDestination)
            ? config with { BackupDestination = DefaultDestination }
            : config;
    }

    public void Save(XivaultConfig config)
    {
        Validate(config);
        var json = JsonSerializer.Serialize(config, XivaultJsonContext.Default.XivaultConfig);
        AtomicFile.WriteAllText(ConfigPath, json);
    }

    public static void Validate(XivaultConfig config)
    {
        if (config.RetentionCount is < XivaultConfig.MinRetentionCount or > XivaultConfig.MaxRetentionCount)
        {
            throw new XivaultException(
                XivaultErrorKind.InvalidConfiguration,
                $"retentionCount must be between {XivaultConfig.MinRetentionCount} and {XivaultConfig.MaxRetentionCount}.");
        }

        if (!TimeOnly.TryParseExact(config.Schedule.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw new XivaultException(XivaultErrorKind.InvalidConfiguration, "schedule.time must be a 24-hour time such as 18:30.");
        }

        if (config.Schedule.Frequency == ScheduleFrequency.Weekly && config.Schedule.Days.Count == 0)
        {
            throw new XivaultException(XivaultErrorKind.InvalidConfiguration, "A weekly schedule needs at least one day.");
        }

        if (config.BackupDestination is { Length: > 0 } destination && !Path.IsPathFullyQualified(destination))
        {
            throw new XivaultException(XivaultErrorKind.InvalidConfiguration, "backupDestination must be a full path, such as D:\\XIVault.");
        }

        if (config.XivLauncherPathOverride is { Length: > 0 } source && !Path.IsPathFullyQualified(source))
        {
            throw new XivaultException(XivaultErrorKind.InvalidConfiguration, "xivLauncherPathOverride must be a full path.");
        }
    }
}

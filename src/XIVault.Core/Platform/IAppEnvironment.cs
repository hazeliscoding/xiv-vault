namespace XIVault.Core.Platform;

/// <summary>Well-known folders and identity of the current user.</summary>
public interface IAppEnvironment
{
    /// <summary>%APPDATA%, where XIVLauncher keeps its configuration.</summary>
    string RoamingAppData { get; }

    /// <summary>%LOCALAPPDATA%, where XIVLauncher installs its executables.</summary>
    string LocalAppData { get; }

    string UserProfile { get; }

    string Documents { get; }

    /// <summary>The OneDrive folder when OneDrive is set up, otherwise null.</summary>
    string? OneDrive { get; }

    string TempPath { get; }

    /// <summary>DOMAIN\user, used as the principal of the scheduled task.</summary>
    string UserAccount { get; }

    /// <summary>Folder for XIVault's own config, state and logs.</summary>
    string DataDirectory { get; }
}

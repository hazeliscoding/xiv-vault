namespace XIVault.Core;

/// <summary>
/// Failure categories shared by every front end. The numeric values are the CLI exit codes,
/// so they must never be renumbered.
/// </summary>
public enum XivaultErrorKind
{
    Unexpected = 1,
    InvalidConfiguration = 2,
    XivLauncherNotFound = 3,
    BackupValidationFailed = 4,
    RestoreValidationFailed = 5,
    GameRunning = 6,
    DestinationUnavailable = 7,
}

public sealed class XivaultException(XivaultErrorKind kind, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public XivaultErrorKind Kind { get; } = kind;
}

using System.Security.Cryptography;
using System.Text;

namespace XIVault.Core.Platform;

/// <summary>
/// Lets only one backup, restore or retention pass run at a time, across processes: the scheduled
/// task can start while the app is restoring. One lock per XIVault data folder.
/// </summary>
public sealed class OperationLock(IAppEnvironment environment)
{
    public static readonly TimeSpan DefaultWait = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Waits for the lock. The returned handle must be disposed on the same thread, which holds as
    /// long as the caller does its work synchronously. Re-entering on the same thread is allowed.
    /// </summary>
    public IDisposable Acquire(TimeSpan? wait = null)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(environment.DataDirectory.ToUpperInvariant())))[..16];
        var mutex = new Mutex(initiallyOwned: false, @"Local\XIVault-" + key);
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(wait ?? DefaultWait);
        }
        catch (AbandonedMutexException)
        {
            // The previous holder exited without releasing it; the lock is ours now.
            acquired = true;
        }

        if (!acquired)
        {
            mutex.Dispose();
            throw new XivaultException(
                XivaultErrorKind.Unexpected,
                "Another XIVault backup or restore is still running. Try again when it finishes.");
        }

        return new Handle(mutex);
    }

    private sealed class Handle(Mutex mutex) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}

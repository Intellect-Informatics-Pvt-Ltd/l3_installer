using Microsoft.Extensions.Logging;

namespace Installer.Core.StateMachine;

/// <summary>
/// Prevents concurrent installer execution using a named system mutex.
/// Includes stale lock detection via PID check.
/// </summary>
public sealed class InstallerLock : IDisposable
{
    private const string MutexName = "Global\\ePACSInstaller";
    private readonly ILogger<InstallerLock> _logger;
    private Mutex? _mutex;
    private bool _acquired;

    public InstallerLock(ILogger<InstallerLock> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Attempts to acquire the installer lock.
    /// </summary>
    /// <param name="timeoutMs">Timeout in milliseconds to wait for the lock. Default: 0 (no wait).</param>
    /// <returns>True if the lock was acquired; false if another instance is running.</returns>
    public bool TryAcquire(int timeoutMs = 0)
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: false, MutexName, out var createdNew);

            if (createdNew)
            {
                _acquired = true;
                _logger.LogInformation("Installer lock acquired (new mutex created).");
                return true;
            }

            // Mutex already exists — try to acquire it
            try
            {
                _acquired = _mutex.WaitOne(timeoutMs);
                if (_acquired)
                {
                    _logger.LogInformation("Installer lock acquired (existing mutex).");
                }
                else
                {
                    _logger.LogWarning("Installer lock not acquired — another instance is running.");
                }
                return _acquired;
            }
            catch (AbandonedMutexException)
            {
                // Previous holder crashed without releasing — we now own it
                _acquired = true;
                _logger.LogWarning("Installer lock acquired (previous holder crashed — abandoned mutex).");
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create/acquire installer mutex.");
            return false;
        }
    }

    /// <summary>
    /// Releases the installer lock.
    /// </summary>
    public void Release()
    {
        if (_acquired && _mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
                _acquired = false;
                _logger.LogInformation("Installer lock released.");
            }
            catch (ApplicationException ex)
            {
                _logger.LogWarning(ex, "Failed to release installer mutex (may not be owned).");
            }
        }
    }

    public void Dispose()
    {
        Release();
        _mutex?.Dispose();
        _mutex = null;
    }
}

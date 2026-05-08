namespace SharedKernel.ErrorHandling;

/// <summary>
/// Base exception for all installer-specific errors.
/// Carries a stable error code, category, severity, and retryable flag.
/// Designed to integrate with Intellect.Erp.ErrorHandling when available,
/// or work standalone with the same contract.
/// </summary>
public class InstallerException : Exception
{
    public InstallerException(
        string errorCode,
        string category,
        string severity,
        bool retryable,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        Category = category;
        Severity = severity;
        Retryable = retryable;
    }

    /// <summary>Stable error code (e.g., ERP-INST-PRE-0001).</summary>
    public string ErrorCode { get; }

    /// <summary>Error category: Validation, System, Dependency, DataIntegrity, Integration, Conflict.</summary>
    public string Category { get; }

    /// <summary>Severity: Info, Warning, Error, Critical.</summary>
    public string Severity { get; }

    /// <summary>Whether the operation can be retried.</summary>
    public bool Retryable { get; }

    /// <summary>Correlation ID captured at throw time.</summary>
    public string? CorrelationId { get; set; }
}

/// <summary>Precheck validation failure (E001–E099).</summary>
public sealed class PrecheckException : InstallerException
{
    public PrecheckException(string errorCode, string message, bool retryable = false, Exception? innerException = null)
        : base(errorCode, "Validation", "Error", retryable, message, innerException) { }
}

/// <summary>Installation action failure (E100–E199).</summary>
public sealed class InstallException : InstallerException
{
    public InstallException(string errorCode, string message, bool retryable = true, Exception? innerException = null)
        : base(errorCode, "System", "Error", retryable, message, innerException) { }
}

/// <summary>Schema migration failure (E200–E299).</summary>
public sealed class MigrationException : InstallerException
{
    public MigrationException(string errorCode, string message, Exception? innerException = null)
        : base(errorCode, "DataIntegrity", "Critical", false, message, innerException) { }
}

/// <summary>Backup or restore failure (E300–E399).</summary>
public sealed class BackupRestoreException : InstallerException
{
    public BackupRestoreException(string errorCode, string message, bool retryable = true, Exception? innerException = null)
        : base(errorCode, "Dependency", "Error", retryable, message, innerException) { }
}

/// <summary>Sync/connectivity failure (E400–E499).</summary>
public sealed class SyncException : InstallerException
{
    public SyncException(string errorCode, string message, Exception? innerException = null)
        : base(errorCode, "Integration", "Warning", true, message, innerException) { }
}

/// <summary>Health monitoring failure (E500–E599).</summary>
public sealed class HealthException : InstallerException
{
    public HealthException(string errorCode, string message, bool retryable = true, Exception? innerException = null)
        : base(errorCode, "Dependency", "Error", retryable, message, innerException) { }
}

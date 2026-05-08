namespace SharedKernel.ErrorHandling;

/// <summary>
/// Factory for creating typed installer exceptions from the error catalog.
/// Stamps correlation ID automatically at creation time.
/// Designed to be compatible with Intellect.Erp.ErrorHandling.IErrorFactory.
/// </summary>
public interface IInstallerErrorFactory
{
    /// <summary>Creates a PrecheckException from the error catalog.</summary>
    PrecheckException Precheck(string errorCode, string? messageOverride = null, Exception? innerException = null);

    /// <summary>Creates an InstallException from the error catalog.</summary>
    InstallException Install(string errorCode, string? messageOverride = null, Exception? innerException = null);

    /// <summary>Creates a MigrationException from the error catalog.</summary>
    MigrationException Migration(string errorCode, string? messageOverride = null, Exception? innerException = null);

    /// <summary>Creates a BackupRestoreException from the error catalog.</summary>
    BackupRestoreException BackupRestore(string errorCode, string? messageOverride = null, Exception? innerException = null);

    /// <summary>Creates a SyncException from the error catalog.</summary>
    SyncException Sync(string errorCode, string? messageOverride = null, Exception? innerException = null);

    /// <summary>Creates a HealthException from the error catalog.</summary>
    HealthException Health(string errorCode, string? messageOverride = null, Exception? innerException = null);

    /// <summary>Creates an InstallerException from any catalog error code.</summary>
    InstallerException FromCatalog(string errorCode, string? messageOverride = null, Exception? innerException = null);
}

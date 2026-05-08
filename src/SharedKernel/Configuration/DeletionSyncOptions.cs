namespace SharedKernel.Configuration;

/// <summary>
/// Configuration for deletion and amendment sync behavior.
/// Controls how hard deletes and in-place updates are captured for NLDR synchronization.
/// Binds to the <c>SyncDeletion</c> section of appsettings.json.
/// </summary>
public sealed class DeletionSyncOptions
{
    public const string SectionName = "SyncDeletion";

    /// <summary>Whether to capture DELETE events in the sync outbox.</summary>
    public bool CaptureDeleteEvents { get; set; } = true;

    /// <summary>Whether to capture AMENDMENT events in the sync outbox.</summary>
    public bool CaptureAmendmentEvents { get; set; } = true;

    /// <summary>Whether to use MySQL triggers for delete capture (fallback for non-application deletes).</summary>
    public bool UseTriggers { get; set; }

    /// <summary>Whether to use application-level capture (recommended).</summary>
    public bool UseApplicationCapture { get; set; } = true;

    /// <summary>Whether amendment events require a reason (mandatory for financial data).</summary>
    public bool AmendmentRequiresReason { get; set; } = true;

    /// <summary>Whether amendment events require an approver (maker-checker enforcement).</summary>
    public bool AmendmentRequiresApprover { get; set; } = true;

    /// <summary>
    /// Number of records above which a deletion is considered "bulk" and requires backup.
    /// Default: 10 records.
    /// </summary>
    public int BulkDeleteThreshold { get; set; } = 10;

    /// <summary>Whether bulk deletes above threshold require a mandatory backup first.</summary>
    public bool BulkDeleteRequiresBackup { get; set; } = true;

    /// <summary>Number of days to retain deleted record state in the outbox (for reconciliation).</summary>
    public int RetainDeletedStateInOutboxDays { get; set; } = 365;

    /// <summary>
    /// Tables that are exempt from delete capture (e.g., temp tables, Hangfire tables).
    /// Deletions from these tables are not synced.
    /// </summary>
    public string[] ExemptTables { get; set; } =
    [
        "fa_vouchermaintemp",
        "fa_voucherdetailstemp",
        "HangfireJob",
        "HangfireJobQueue",
        "HangfireJobState",
        "HangfireJobParameter",
        "HangfireState",
        "HangfireCounter",
        "HangfireAggregatedCounter",
        "DistributedLock",
        "HangfireDistributedLock"
    ];
}

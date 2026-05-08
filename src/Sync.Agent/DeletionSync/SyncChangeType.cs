namespace Sync.Agent.DeletionSync;

/// <summary>
/// Types of data changes that must be propagated to NLDR.
/// Every deletion and amendment is an EVENT — nothing is ever truly lost from the sync perspective.
/// </summary>
public enum SyncChangeType
{
    /// <summary>New record created.</summary>
    Insert,

    /// <summary>Existing record modified (non-financial routine update).</summary>
    Update,

    /// <summary>Record physically deleted (hard delete). Before-state captured in payload.</summary>
    Delete,

    /// <summary>Auditor/correction amendment to financial data. Before+after state + reason + approver.</summary>
    Amendment
}

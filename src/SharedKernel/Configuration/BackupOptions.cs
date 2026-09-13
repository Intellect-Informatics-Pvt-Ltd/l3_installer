namespace SharedKernel.Configuration;

/// <summary>
/// Configuration for backup and restore operations.
/// Binds to the <c>Backup</c> section of appsettings.json.
/// </summary>
public sealed class BackupOptions
{
    public const string SectionName = "Backup";

    /// <summary>Target directories for backup storage. First available is used.</summary>
    public string[] Targets { get; set; } = ["${DataRoot}\\backups"];

    /// <summary>Backup schedule configuration.</summary>
    public BackupScheduleOptions Schedule { get; set; } = new();

    /// <summary>Backup retention policy.</summary>
    public BackupRetentionOptions Retention { get; set; } = new();

    /// <summary>Encryption configuration for backup packages.</summary>
    public BackupEncryptionOptions Encryption { get; set; } = new();

    /// <summary>
    /// Database size threshold (in GB) above which mysqlsh util.dumpInstance is used
    /// instead of mysqldump. Below this threshold, mysqldump is used.
    /// </summary>
    public int LargeDbThresholdGb { get; set; } = 5;

    /// <summary>
    /// Minimum free space multiplier on backup target.
    /// Backup target must have at least (estimated_backup_size * this value) free.
    /// </summary>
    public double TargetFreeSpaceMultiplier { get; set; } = 1.5;

    /// <summary>
    /// Whether to warn if backup target is on the same physical volume as data.
    /// </summary>
    public bool WarnOnSameVolume { get; set; } = true;
}

public sealed class BackupScheduleOptions
{
    /// <summary>Time of day for daily backup (HH:mm format).</summary>
    public string Daily { get; set; } = "02:00";

    /// <summary>Day of week for weekly full backup.</summary>
    public string WeeklyDay { get; set; } = "Sunday";

    /// <summary>Time of day for weekly full backup (HH:mm format).</summary>
    public string WeeklyTime { get; set; } = "03:00";
}

public sealed class BackupRetentionOptions
{
    /// <summary>Number of daily incremental backups to retain.</summary>
    public int DailyCount { get; set; } = 7;

    /// <summary>Number of weekly full backups to retain.</summary>
    public int WeeklyCount { get; set; } = 4;

    /// <summary>Number of pre-upgrade backups to retain.</summary>
    public int PreUpgradeCount { get; set; } = 2;
}

public sealed class BackupEncryptionOptions
{
    /// <summary>Encryption algorithm for backup packages.</summary>
    public string Algorithm { get; set; } = "AES-256-GCM";

    /// <summary>Certificate thumbprint for key wrapping (enables restore-to-new-machine).</summary>
    public string? CertificateThumbprint { get; set; }

    /// <summary>
    /// Path to the state's recovery PUBLIC key (PEM). When set, every backup's data key is also
    /// wrapped to it, so a re-imaged or replacement node can be restored by the holder of the
    /// private key. Without it a backup can be restored only by the node that wrote it - which
    /// is stated in every manifest as <c>KeyProtection</c>, never assumed.
    /// </summary>
    public string? RecoveryPublicKeyPath { get; set; }

    /// <summary>
    /// Path to the state's recovery PRIVATE key (PEM), for a restore on a node that cannot
    /// unwrap locally. Supplied at restore time, never stored on the node.
    /// </summary>
    public string? RecoveryPrivateKeyPath { get; set; }
}

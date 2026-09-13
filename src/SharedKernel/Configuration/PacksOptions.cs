namespace SharedKernel.Configuration;

/// <summary>
/// How data leaves and re-enters the node (ADR-0011). Binds to the <c>Packs</c> section.
/// </summary>
public sealed class PacksOptions
{
    public const string SectionName = "Packs";

    /// <summary>
    /// <c>packs</c> (the only mode that exists) or <c>stream</c> (the frozen Kafka→NLDR path,
    /// refused at start with exit 4 naming ADR-0011).
    /// </summary>
    public string Mode { get; set; } = "packs";

    /// <summary>Where packs live: <c>outbound/</c>, <c>inbound/</c>, <c>applied/</c>, <c>rejected/</c> beneath it.</summary>
    public string Root { get; set; } = "${DataRoot}/packs";

    /// <summary>The ledger of what was produced and applied: <c>&lt;DataRoot&gt;/sync/pack-ledger.json</c>.</summary>
    public string LedgerPath { get; set; } = "${DataRoot}/sync/pack-ledger.json";

    /// <summary>The medium's <c>config/pacs-table-classification.json</c>, copied to the node at install.</summary>
    public string ClassificationPath { get; set; } = "${DataRoot}/config/pacs-table-classification.json";

    /// <summary>PKCS#12 with the site's signing key (issued by the state, delivered with the site pack). Without it packs are written UNSIGNED and say so.</summary>
    public string? SigningPfxPath { get; set; }

    /// <summary>Environment variable holding the PFX password. Never the password itself.</summary>
    public string SigningPfxPasswordEnv { get; set; } = "EPACS_SITE_PFX_PASSWORD";

    /// <summary>The state's public key (PEM). When set, ledger packs are encrypted to it; without it they are signed only and the manifest says <c>encryption: none</c>.</summary>
    public string? RecipientPublicKeyPath { get; set; }

    /// <summary>Which signer inbound packs must carry. Defaults to the release key (Installer:ExpectedSigningThumbprint).</summary>
    public string? InboundSignerThumbprint { get; set; }

    /// <summary>Refuse an unsigned inbound pack. Default true; false is for a development bench only.</summary>
    public bool RequireSignedInbound { get; set; } = true;

    /// <summary>Path to this node's private key (PEM) for inbound packs encrypted to it. Not issued in v0; inbound packs are signed, not encrypted.</summary>
    public string? RecipientPrivateKeyPath { get; set; }

    public int ExportIntervalMinutes { get; set; } = 24 * 60;

    public int ApplyIntervalMinutes { get; set; } = 15;

    /// <summary>Reported by the exporter in every manifest.</summary>
    public string ProducerVersion { get; set; } = "epacs-sync/1.0";
}

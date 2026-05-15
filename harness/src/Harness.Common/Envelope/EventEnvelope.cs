using System.Text.Json.Serialization;

namespace Harness.Common.Envelope;

/// <summary>
/// Wire envelope that carries a single business event from PACS to NLDR
/// (§7.1 of the design overview).
/// </summary>
public sealed record EventEnvelope
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "1.0";

    [JsonPropertyName("eventId")]
    public string EventId { get; init; } = string.Empty;

    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; init; } = string.Empty;

    [JsonPropertyName("causationId")]
    public string? CausationId { get; init; }

    [JsonPropertyName("pacsId")]
    public string PacsId { get; init; } = string.Empty;

    [JsonPropertyName("sourceSystem")]
    public string SourceSystem { get; init; } = "PACS";

    [JsonPropertyName("targetSystem")]
    public string TargetSystem { get; init; } = "NLDR";

    [JsonPropertyName("sequenceNo")]
    public long SequenceNo { get; init; }

    [JsonPropertyName("streamName")]
    public string StreamName { get; init; } = "pacs.outbound";

    [JsonPropertyName("idempotencyKey")]
    public string IdempotencyKey { get; init; } = string.Empty;

    [JsonPropertyName("changeType")]
    public ChangeType ChangeType { get; init; }

    [JsonPropertyName("entityType")]
    public string EntityType { get; init; } = string.Empty;

    [JsonPropertyName("entityId")]
    public string EntityId { get; init; } = string.Empty;

    /// <summary>After-state (mandatory). Use JSON element to preserve raw
    /// structure without re-serialisation round-trips.</summary>
    [JsonPropertyName("payload")]
    public object? Payload { get; init; }

    /// <summary>Before-state — mandatory for UPDATE / DELETE / AMENDMENT.</summary>
    [JsonPropertyName("beforeState")]
    public object? BeforeState { get; init; }

    /// <summary>AMENDMENT-specific metadata (reason + approver).</summary>
    [JsonPropertyName("amendmentMeta")]
    public AmendmentMeta? AmendmentMeta { get; init; }

    /// <summary>
    /// SHA-256 hex over canonical_json({ payload, beforeState, amendmentMeta }).
    /// Computed by <see cref="Canonicalization.PayloadHasher"/>.
    /// </summary>
    [JsonPropertyName("payloadHash")]
    public string PayloadHash { get; init; } = string.Empty;

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; init; }
}

/// <summary>Possible change types in the transactional outbox.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChangeType
{
    INSERT,
    UPDATE,
    DELETE,
    AMENDMENT
}

/// <summary>Metadata carried on AMENDMENT events.</summary>
public sealed class AmendmentMeta
{
    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;

    [JsonPropertyName("approver")]
    public string Approver { get; init; } = string.Empty;
}

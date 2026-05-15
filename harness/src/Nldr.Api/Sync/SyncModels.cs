using System.Text.Json.Serialization;

namespace Nldr.Api.Sync;

/// <summary>Response returned by <c>POST /api/sync/ingest</c>.</summary>
public sealed class IngestResponse
{
    [JsonPropertyName("eventId")]
    public string EventId { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;     // APPLIED | DUPLICATE | REJECTED | GAP_WAITING

    [JsonPropertyName("ackedAt")]
    public DateTimeOffset? AckedAt { get; init; }

    [JsonPropertyName("rejectReason")]
    public string? RejectReason { get; init; }
}

/// <summary>Possible apply statuses for a received event.</summary>
public static class ApplyStatus
{
    public const string Applied      = "APPLIED";
    public const string Duplicate    = "DUPLICATE";
    public const string Rejected     = "REJECTED";
    public const string GapWaiting   = "GAP_WAITING";
}

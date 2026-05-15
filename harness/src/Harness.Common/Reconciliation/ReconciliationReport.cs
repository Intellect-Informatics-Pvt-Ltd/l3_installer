using System.Text.Json.Serialization;

namespace Harness.Common.Reconciliation;

/// <summary>
/// Structured reconciliation report (§18.2).
/// Saved to <c>{DataRoot}/reconciliation/RUN-{utcDate}.json</c>.
/// </summary>
public sealed class ReconciliationReport
{
    [JsonPropertyName("pacsId")]
    public string PacsId { get; init; } = string.Empty;

    [JsonPropertyName("windowFrom")]
    public DateTimeOffset WindowFrom { get; init; }

    [JsonPropertyName("windowTo")]
    public DateTimeOffset WindowTo { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = ReconciliationStatus.Pass;

    [JsonPropertyName("checks")]
    public IReadOnlyList<ReconciliationCheck> Checks { get; init; } = [];

    [JsonPropertyName("summary")]
    public ReconciliationSummary Summary { get; init; } = new();
}

public static class ReconciliationStatus
{
    public const string Pass = "PASS";
    public const string Fail = "FAIL";
}

public sealed class ReconciliationCheck
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = ReconciliationStatus.Pass;

    [JsonPropertyName("detail")]
    public object? Detail { get; init; }
}

public sealed class ReconciliationSummary
{
    [JsonPropertyName("expected")]
    public long Expected { get; init; }

    [JsonPropertyName("localAck")]
    public long LocalAck { get; init; }

    [JsonPropertyName("centralReceived")]
    public long CentralReceived { get; init; }

    [JsonPropertyName("hashMismatches")]
    public int HashMismatches { get; init; }

    [JsonPropertyName("gaps")]
    public IReadOnlyList<long> Gaps { get; init; } = [];

    [JsonPropertyName("orphans")]
    public IReadOnlyList<string> Orphans { get; init; } = [];

    [JsonPropertyName("duplicates")]
    public IReadOnlyList<string> Duplicates { get; init; } = [];
}

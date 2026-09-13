using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharedKernel.Packs;

/// <summary>
/// <c>db/pacs-table-classification.json</c> from the L2-R2 workspace — which baseline tables are a
/// society's own (partitioned by a PacsId column), which are masters the state owns, and which are
/// excluded from packs altogether. The carve-out and the ledger-pack exporter both read it; a
/// guard in the workspace fails when a PacsId-carrying baseline table is missing from it, so a
/// new table cannot be added unclassified (ADR-0007 estate-side, ADR-0011).
/// </summary>
public sealed record TableClassification
{
    [JsonPropertyName("generated_by")]
    public string? GeneratedBy { get; init; }

    [JsonPropertyName("baseline_table_count")]
    public int BaselineTableCount { get; init; }

    /// <summary>table → the column that carries the society id (usually <c>PacsId</c>; the case is the baseline's).</summary>
    [JsonPropertyName("society")]
    public Dictionary<string, string> Society { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tables the state owns and distributes downward in policy packs.</summary>
    [JsonPropertyName("master")]
    public List<string> Master { get; init; } = [];

    /// <summary>Tables that never travel: runtime, cache, logs, the outboxes themselves.</summary>
    [JsonPropertyName("excluded")]
    public List<string> Excluded { get; init; } = [];

    public static async Task<TableClassification> LoadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "The table classification is missing. Without it the exporter cannot know which tables are a society's own, " +
                "and guessing would either leak another society's rows or omit this one's. It is generated in the L2-R2 " +
                "workspace by build/generate-table-classification.py and carried on the medium as config/pacs-table-classification.json.", path);
        }

        var text = await File.ReadAllTextAsync(path, ct);
        var doc = JsonSerializer.Deserialize<TableClassification>(text) ?? throw new InvalidDataException($"{path} is empty.");
        if (doc.Society.Count == 0)
        {
            throw new InvalidDataException($"{path} classifies no society tables; that cannot be right for this estate (806 carried PacsId on 2026-08-22).");
        }

        return doc;
    }
}

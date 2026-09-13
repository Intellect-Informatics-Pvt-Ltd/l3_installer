using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharedKernel.Packs;

/// <summary>
/// The node's side of exactly-once: what it has produced and what it has applied, per pack
/// type, as one JSON file under <c>&lt;DataRoot&gt;/sync/pack-ledger.json</c>. The state keeps
/// the mirror image in its own table; the two agreeing is what reconciliation means under
/// ADR-0011 (G12 reframed).
///
/// Written whole and renamed into place, so a power cut mid-write cannot leave a ledger that
/// parses as far as the truncation.
/// </summary>
public sealed class PackLedger
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public sealed record Cursor
    {
        public long LastSeq { get; init; }
        public string LastHash { get; init; } = PackManifest.Genesis;
        public DateTimeOffset? At { get; init; }
        public string? LastPackId { get; init; }
    }

    public sealed record State
    {
        /// <summary>What this node has PRODUCED, per pack type name.</summary>
        public Dictionary<string, Cursor> Produced { get; init; } = new(StringComparer.Ordinal);

        /// <summary>What this node has APPLIED, per pack type name.</summary>
        public Dictionary<string, Cursor> Applied { get; init; } = new(StringComparer.Ordinal);
    }

    private readonly string _path;

    public PackLedger(string path) => _path = path;

    public string Path => _path;

    public async Task<State> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
        {
            return new State();
        }

        var text = await File.ReadAllTextAsync(_path, ct);
        return JsonSerializer.Deserialize<State>(text, Json) ?? new State();
    }

    public async Task SaveAsync(State state, CancellationToken ct = default)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(state, Json), ct);
        File.Move(temp, _path, overwrite: true);
    }

    /// <summary>The cursor for the next pack of this type to be produced: seq + 1 and the hash to chain to.</summary>
    public static Cursor NextProduced(State state, PackType type) =>
        state.Produced.GetValueOrDefault(type.ToString()) ?? new Cursor();

    /// <summary>The cursor for the next pack of this type to be applied.</summary>
    public static Cursor NextApplied(State state, PackType type) =>
        state.Applied.GetValueOrDefault(type.ToString()) ?? new Cursor();

    public static void RecordProduced(State state, PackType type, long seq, string hash, string packId) =>
        state.Produced[type.ToString()] = new Cursor { LastSeq = seq, LastHash = hash, At = DateTimeOffset.UtcNow, LastPackId = packId };

    public static void RecordApplied(State state, PackType type, long seq, string hash, string packId) =>
        state.Applied[type.ToString()] = new Cursor { LastSeq = seq, LastHash = hash, At = DateTimeOffset.UtcNow, LastPackId = packId };
}

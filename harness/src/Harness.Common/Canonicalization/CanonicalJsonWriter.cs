using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Harness.Common.Canonicalization;

/// <summary>
/// Produces deterministic, canonical JSON suitable for hashing.
/// Rules (§7.2 of the design overview):
/// <list type="number">
///   <item>Object keys sorted lexicographically (UTF-16 code unit order) at every depth.</item>
///   <item>No insignificant whitespace.</item>
///   <item>Numbers serialised in <c>R</c> round-trip format.</item>
///   <item>Booleans lowercase, nulls as <c>null</c>.</item>
///   <item>Strings escaped per RFC 8259.</item>
///   <item>Output encoded as UTF-8.</item>
/// </list>
/// This is the ONLY class in the solution that may call <see cref="SHA256.HashData"/>.
/// (That invariant is enforced by <see cref="PayloadHasher"/>.)
/// </summary>
public static class CanonicalJsonWriter
{
    private static readonly JsonSerializerOptions _roundTripOptions = new()
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict,
        WriteIndented  = false
    };

    /// <summary>
    /// Serialises <paramref name="value"/> to canonical JSON and returns UTF-8 bytes.
    /// </summary>
    public static byte[] WriteBytes(object? value)
    {
        if (value is null)
            return "null"u8.ToArray();

        // Deserialise to a JsonNode first so we can sort keys recursively.
        var raw = JsonSerializer.SerializeToNode(value, _roundTripOptions)
                  ?? JsonValue.Create((object?)null);

        var builder = new StringBuilder();
        WriteNode(builder, raw!);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>
    /// Serialises <paramref name="value"/> to a canonical JSON string.
    /// </summary>
    public static string WriteString(object? value) =>
        Encoding.UTF8.GetString(WriteBytes(value));

    // ── private helpers ──────────────────────────────────────────────────────

    private static void WriteNode(StringBuilder sb, JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                WriteObject(sb, obj);
                break;
            case JsonArray arr:
                WriteArray(sb, arr);
                break;
            case JsonValue val:
                WriteValue(sb, val);
                break;
        }
    }

    private static void WriteObject(StringBuilder sb, JsonObject obj)
    {
        sb.Append('{');
        var keys = obj.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        for (var i = 0; i < keys.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"');
            sb.Append(EscapeString(keys[i]));
            sb.Append("\":");
            var child = obj[keys[i]];
            if (child is null)
                sb.Append("null");
            else
                WriteNode(sb, child);
        }
        sb.Append('}');
    }

    private static void WriteArray(StringBuilder sb, JsonArray arr)
    {
        sb.Append('[');
        for (var i = 0; i < arr.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var item = arr[i];
            if (item is null) sb.Append("null");
            else WriteNode(sb, item);
        }
        sb.Append(']');
    }

    private static void WriteValue(StringBuilder sb, JsonValue val)
    {
        // Preserve the raw JSON representation for booleans, nulls, and
        // numbers. For strings, re-escape according to RFC 8259.
        var elem = val.GetValue<JsonElement>();
        switch (elem.ValueKind)
        {
            case JsonValueKind.String:
                sb.Append('"');
                sb.Append(EscapeString(elem.GetString() ?? string.Empty));
                sb.Append('"');
                break;

            case JsonValueKind.Number:
                // R-format: try double first; fall back to raw text.
                if (elem.TryGetDouble(out var d))
                    sb.Append(d.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                else
                    sb.Append(elem.GetRawText());
                break;

            case JsonValueKind.True:
                sb.Append("true");
                break;

            case JsonValueKind.False:
                sb.Append("false");
                break;

            default: // Null, Undefined
                sb.Append("null");
                break;
        }
    }

    /// <summary>Escapes a string per RFC 8259 §7.</summary>
    private static string EscapeString(string s)
    {
        // System.Text.Json handles this correctly when writing via JsonSerializer.
        // We use it to produce a quoted+escaped string and then strip the quotes.
        var opts = new JsonWriterOptions { Indented = false };
        using var ms     = new System.IO.MemoryStream();
        using var writer = new Utf8JsonWriter(ms, opts);
        writer.WriteStringValue(s);
        writer.Flush();
        var bytes  = ms.ToArray();
        // bytes is: "<escaped>" — strip the surrounding quotes
        return Encoding.UTF8.GetString(bytes, 1, bytes.Length - 2);
    }
}

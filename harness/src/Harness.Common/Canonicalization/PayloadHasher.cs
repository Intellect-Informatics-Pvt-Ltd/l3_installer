using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Harness.Common.Envelope;

namespace Harness.Common.Canonicalization;

/// <summary>
/// Computes the tamper-evident hash for a sync event (§7.3).
/// Formula:
/// <code>
/// payloadHash = sha256_hex(
///     canonical_json({ "payload": payload, "beforeState": beforeState, "amendmentMeta": amendmentMeta })
/// )
/// </code>
/// <para>
/// This is the ONLY class in the solution that calls <see cref="SHA256.HashData"/>.
/// Reviewers should grep for <c>SHA256.HashData</c> and verify it appears only here.
/// </para>
/// </summary>
public static class PayloadHasher
{
    /// <summary>
    /// Computes the lowercase hex SHA-256 hash over the canonical JSON of the
    /// combined hash-input object.
    /// </summary>
    public static string Compute(object? payload, object? beforeState, AmendmentMeta? amendmentMeta)
    {
        var hashInput = new HashInput(payload, beforeState, amendmentMeta);
        var canonicalBytes = CanonicalJsonWriter.WriteBytes(hashInput);
        var hashBytes = SHA256.HashData(canonicalBytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Validates that <paramref name="envelope"/>'s stored hash matches
    /// the recomputed hash. Returns <c>true</c> when they match.
    /// </summary>
    public static bool Verify(EventEnvelope envelope)
    {
        var recomputed = Compute(envelope.Payload, envelope.BeforeState, envelope.AmendmentMeta);
        return string.Equals(recomputed, envelope.PayloadHash, StringComparison.OrdinalIgnoreCase);
    }

    // ── private helper ───────────────────────────────────────────────────────

    /// <summary>
    /// Wrapper that controls the exact keys included in the hash input.
    /// <c>amendmentMeta</c> is omitted when null so the hash stays stable
    /// across events that don't carry amendment metadata.
    /// </summary>
    private sealed class HashInput
    {
        [JsonPropertyName("amendmentMeta")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AmendmentMeta? AmendmentMeta { get; }

        [JsonPropertyName("beforeState")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? BeforeState { get; }

        [JsonPropertyName("payload")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Payload { get; }

        public HashInput(object? payload, object? beforeState, AmendmentMeta? amendmentMeta)
        {
            // Keys are alphabetically ordered: amendmentMeta, beforeState, payload —
            // but CanonicalJsonWriter re-sorts them anyway, so order here doesn't matter.
            Payload       = payload;
            BeforeState   = beforeState;
            AmendmentMeta = amendmentMeta;
        }
    }
}

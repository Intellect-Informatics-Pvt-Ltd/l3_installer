using System.Text.RegularExpressions;

namespace Harness.Common.Identifiers;

/// <summary>
/// Formats and parses the idempotency key used to detect duplicate events
/// across retries with different <c>eventId</c>s (§7.4 of the design).
/// <para>
/// Format: <c>{pacsId}:{entityType}:{entityId}:{changeType}:{businessTimestampISO8601}</c>
/// </para>
/// </summary>
public static partial class IdempotencyKey
{
    // Pattern for validation (also used in contract tests).
    [GeneratedRegex(
        @"^[A-Z0-9-]+:[a-z_]+:[^:]+:(INSERT|UPDATE|DELETE|AMENDMENT):\d{4}-\d{2}-\d{2}T",
        RegexOptions.Compiled)]
    public static partial Regex ValidPattern();

    /// <summary>Formats the idempotency key from its components.</summary>
    public static string Format(
        string pacsId,
        string entityType,
        string entityId,
        string changeType,
        DateTimeOffset businessTimestamp) =>
        $"{pacsId}:{entityType}:{entityId}:{changeType}:{businessTimestamp:yyyy-MM-ddTHH:mm:ssZ}";

    /// <summary>
    /// Parses the key back to its components.
    /// Returns <c>false</c> when the key does not match the expected format.
    /// </summary>
    public static bool TryParse(
        string key,
        out string pacsId,
        out string entityType,
        out string entityId,
        out string changeType,
        out DateTimeOffset businessTimestamp)
    {
        pacsId = entityType = entityId = changeType = string.Empty;
        businessTimestamp = default;

        var parts = key.Split(':', 5);
        if (parts.Length != 5) return false;

        pacsId    = parts[0];
        entityType = parts[1];
        entityId  = parts[2];
        changeType = parts[3];

        return DateTimeOffset.TryParse(parts[4], null,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out businessTimestamp);
    }
}

/// <summary>Generates unique event identifiers (UUIDv4).</summary>
public static class EventIdProvider
{
    /// <summary>Returns a new, globally unique event ID (UUIDv4).</summary>
    public static string NewEventId() => Guid.NewGuid().ToString("D");
}

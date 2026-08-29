using System.Globalization;

namespace Installer.Core.Schema;

/// <summary>
/// Reads <c>db/known-drift.txt</c> — the estate's register of drift consciously accepted.
///
/// The format is the estate's, unchanged: one entry per line, <c>&lt;key&gt;TAB&lt;reason&gt;</c>,
/// with <c>#</c> comments and blanks ignored. Reading the same file the online tooling reads is
/// the whole point: a divergence a DBA has already dispositioned should not stop an upgrade at a
/// node, and it should not need a second register that can disagree with the first.
///
/// The file's own header explains the discipline this preserves:
///
///   <i>"A STALE ENTRY IS NOT HARMLESS HOUSEKEEPING. It is a standing, pre-approved exemption
///   for a finding that no longer exists — so the day that drift comes back, the gate greets it
///   with ACKNOWLEDGED and exits 0. That is strictly worse than having no exemption at all."</i>
///
/// So <see cref="SchemaDriftReport.StaleAcknowledgements"/> reports every entry that matched
/// nothing, and the caller is expected to say so.
/// </summary>
public static class KnownDriftRegister
{
    /// <summary>Parses register text. Returns key → reason, keys compared case-insensitively.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            // Tab-separated, as the estate writes it. A line without a tab is malformed rather
            // than key-only: an exemption with no stated reason is exactly what the register's
            // "deliberately tedious to add to" rule exists to prevent, so it is skipped rather
            // than honoured.
            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab <= 0)
            {
                continue;
            }

            var key = line[..tab].Trim();
            var reason = line[(tab + 1)..].Trim();

            if (key.Length > 0 && reason.Length > 0)
            {
                entries[key] = reason;
            }
        }

        return entries;
    }

    /// <summary>
    /// Loads the register from disk. A missing file is an empty register, not an error: a node
    /// may legitimately have no acknowledged drift, and refusing to run without the file would
    /// make an empty register harder to have than a populated one.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return File.Exists(path)
            ? Parse(await File.ReadAllTextAsync(path, cancellationToken))
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Renders a report for an operator or a support bundle. Acknowledged findings are shown,
    /// not hidden — the point of the register is that someone decided, not that nobody looks.
    /// </summary>
    public static string Render(SchemaDriftReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new System.Text.StringBuilder();
        var c = CultureInfo.InvariantCulture;

        var unacknowledged = report.Unacknowledged.ToList();
        var acknowledged = report.Findings.Where(f => f.IsAcknowledged).ToList();

        sb.AppendLine(c, $"Schema drift: {unacknowledged.Count} finding(s), {acknowledged.Count} acknowledged. Verdict: {report.Severity}.");

        foreach (var group in unacknowledged.GroupBy(f => f.Severity).OrderByDescending(g => g.Key))
        {
            sb.AppendLine();
            sb.AppendLine(c, $"  {group.Key.ToString().ToUpperInvariant()}");
            foreach (var f in group.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                sb.AppendLine(c, $"    {f.Key}");
                sb.AppendLine(c, $"      {f.Message}");
            }
        }

        if (acknowledged.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  ACKNOWLEDGED (db/known-drift.txt)");
            foreach (var f in acknowledged.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                sb.AppendLine(c, $"    {f.Key} — {f.AcknowledgementReason}");
            }
        }

        if (report.StaleAcknowledgements.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  STALE ACKNOWLEDGEMENTS — these matched nothing, and a standing exemption for a finding");
            sb.AppendLine("  that no longer exists will silently pass the day that drift returns:");
            foreach (var key in report.StaleAcknowledgements)
            {
                sb.AppendLine(c, $"    {key}");
            }
        }

        return sb.ToString();
    }
}

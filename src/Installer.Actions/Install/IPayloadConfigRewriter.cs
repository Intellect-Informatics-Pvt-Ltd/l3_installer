using SharedKernel.Contracts;

namespace Installer.Actions.Install;

/// <summary>
/// Writes the node's facts INTO each deployed service's own <c>appsettings.json</c>: the site
/// overlay (database, cache, log path, identity), the sibling-URL rewrites, and the application
/// database password.
///
/// WHY THE FILE, AND NOT THE ENVIRONMENT. The estate's ADR-0002 (configuration resolution,
/// 2026-09-06) gave modules an <c>ErpConfig</c> root that reads the environment after the file —
/// and measured on 2026-09-13, adoption is partial: ERPClient reads through it at 719 sites,
/// while FAS still has 24 and Loans 47 call sites building a raw <c>ConfigurationBuilder</c> over
/// <c>appsettings.json</c> alone. A value set only in the environment is therefore read by some
/// code paths and not others, in the same process. The only place every code path reads is the
/// file, so that is where the node's facts go. When adoption reaches 100% this can become
/// <c>Environment=</c> lines in the unit; nothing else changes.
///
/// WHY THE PASSWORD IS HERE. The template's original intent was "no password on disk; injected
/// at registration". Injection means the environment, and the environment is not read by every
/// path (above). A service that reaches the database from 719 sites and not from 24 is a service
/// that starts, passes its health check, and fails on the first voucher. So the password is
/// written, and the compensating controls are stated rather than assumed: the file is mode 0640,
/// owned by the service account, generated on the node and never committed; the support bundle
/// redacts connection strings; repair rewrites it from the secret store.
/// </summary>
public interface IPayloadConfigRewriter
{
    /// <param name="servicesRoot">The release's <c>services/</c> directory — one subdirectory per application service.</param>
    /// <param name="siteOverlayPath">The generated <c>appsettings.Site.json</c>; null to skip the overlay (harness media).</param>
    /// <param name="siblingUrlsPath">The medium's <c>config/sibling-urls.json</c>; null when the medium carries none.</param>
    /// <param name="services">The topology; only entries with a <c>services/&lt;name&gt;/appsettings.json</c> are rewritten.</param>
    Task<PayloadConfigResult> RewriteAsync(
        string servicesRoot,
        string? siteOverlayPath,
        string? siblingUrlsPath,
        IReadOnlyList<ServiceMapEntry> services,
        CancellationToken cancellationToken = default);
}

public sealed record PayloadConfigResult
{
    /// <summary>Services whose appsettings.json was rewritten, with the number of values each received.</summary>
    public required IReadOnlyDictionary<string, int> Rewritten { get; init; }

    /// <summary>Map entries with no appsettings.json under services/, left alone (infrastructure, agents).</summary>
    public required IReadOnlyList<string> Skipped { get; init; }

    public bool PasswordWritten { get; init; }
}

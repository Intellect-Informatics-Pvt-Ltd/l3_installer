using SharedKernel.Contracts;

namespace Installer.Actions.Install;

/// <summary>
/// The site's own tokens — <c>${epcfg:state_code}</c> and friends — made available to every
/// consumer of the token vocabulary, not only the config generator.
///
/// WHY THIS EXISTS. Until 2026-09-13 the service orchestrators resolved tokens from
/// <see cref="InstallerTokenMap.BuildInfrastructure"/> alone, so a service-map entry carrying
/// <c>ASPNETCORE_ENVIRONMENT: ${epcfg:state_code}</c> — the estate's entire state-selection
/// mechanism — could not be registered: the token was unresolved and registration aborted.
/// The generated L2-R2 topology carries exactly that token on all 27 application services, so
/// the site identity has to reach the orchestrator. It reaches it here, bound once by whichever
/// engine loaded the <c>.epcfg</c>, read by whoever resolves a service-map entry.
///
/// Unbound is not an error at construction — a dry run without a site pack must still compose —
/// but it IS an error at resolution: an unresolved <c>${epcfg:...}</c> aborts, naming the token,
/// exactly as any other unresolved token does. Nothing is defaulted, because a defaulted state
/// runs the wrong state's configuration without failing.
/// </summary>
public interface ISiteTokenSource
{
    /// <summary>The bound site's tokens, or an empty map when no site pack has been loaded.</summary>
    IReadOnlyDictionary<string, string> Tokens { get; }

    /// <summary>The site this process is acting for, once known.</summary>
    SiteConfigPack? Site { get; }

    void Bind(SiteConfigPack site);
}

public sealed class SiteTokenSource : ISiteTokenSource
{
    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private volatile IReadOnlyDictionary<string, string> _tokens = Empty;

    public IReadOnlyDictionary<string, string> Tokens => _tokens;

    public SiteConfigPack? Site { get; private set; }

    public void Bind(SiteConfigPack site)
    {
        ArgumentNullException.ThrowIfNull(site);
        Site = site;
        _tokens = InstallerTokenMap.BuildSite(site);
    }
}

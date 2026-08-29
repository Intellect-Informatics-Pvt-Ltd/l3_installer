using SharedKernel.Contracts;

namespace Installer.Actions.Topology;

/// <summary>
/// Loads a service map (the payload topology) into <see cref="ServiceMapEntry"/> records for
/// <c>IServiceOrchestrator</c> to register, start, stop and monitor.
///
/// This is the framework's only topology input. Everything downstream — registration order,
/// shutdown order, health probing, recovery actions, ACL targets — is derived from what this
/// returns, so a payload is described here and nowhere else in code.
///
/// It reads both maps in this repository: the infrastructure map (<c>samples/service-map.yaml</c>,
/// which uses <c>command</c> and <c>tcp</c> health checks) and the harness map
/// (<c>harness/packaging/service-map.yaml</c>, which uses <c>http</c> and carries <c>group</c>
/// and <c>dependencies</c>). A real payload's map is expected to be generated, not hand-written.
/// </summary>
public interface IServiceMapLoader
{
    /// <summary>Reads and parses a service-map YAML file.</summary>
    /// <param name="serviceMapPath">Path to the YAML file.</param>
    /// <param name="groups">
    /// When non-empty, only services whose <c>group</c> is in this set are returned. Services
    /// with no <c>group</c> are always returned — an ungrouped map is a single-group map.
    /// </param>
    Task<IReadOnlyList<ServiceMapEntry>> LoadAsync(
        string serviceMapPath,
        IReadOnlyCollection<string>? groups = null,
        CancellationToken cancellationToken = default);

    /// <summary>Parses service-map YAML content. Throws <see cref="ServiceMapException"/> on invalid input.</summary>
    IReadOnlyList<ServiceMapEntry> Parse(string yamlContent, IReadOnlyCollection<string>? groups = null);
}

/// <summary>
/// Raised when a service map cannot be parsed or fails validation.
///
/// Deliberately fatal rather than skip-and-continue: a map that silently drops a service
/// produces a node that installs "successfully" with a module missing, and that surfaces days
/// later as a functional defect rather than as an install failure.
/// </summary>
public sealed class ServiceMapException : Exception
{
    public ServiceMapException(string message) : base(message) { }
    public ServiceMapException(string message, Exception inner) : base(message, inner) { }
    public ServiceMapException() { }
}

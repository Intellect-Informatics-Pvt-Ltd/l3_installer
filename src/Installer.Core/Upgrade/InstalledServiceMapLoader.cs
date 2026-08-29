using Installer.Actions.Topology;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;
using SharedKernel.Contracts;

namespace Installer.Core.Upgrade;

/// <summary>
/// Loads the topology of the INSTALLED release, for the upgrade and rollback paths.
///
/// Reads the service map from under <c>current</c> rather than from the new media: an upgrade
/// has to stop the services that are running now, and those are described by the map that was
/// installed with them. Taking the new release's map would risk stopping a set of services that
/// does not match what is actually registered — and leaving the difference running.
/// </summary>
public sealed class InstalledServiceMapLoader : IServiceMapLoaderAdapter
{
    private readonly IServiceMapLoader _loader;
    private readonly IOptions<InstallerOptions> _options;
    private readonly IOptions<ComponentsOptions> _components;

    public InstalledServiceMapLoader(
        IServiceMapLoader loader,
        IOptions<InstallerOptions> options,
        IOptions<ComponentsOptions> components)
    {
        _loader = loader;
        _options = options;
        _components = components;
    }

    public Task<IReadOnlyList<ServiceMapEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var opts = _options.Value;
        var path = Path.IsPathRooted(opts.ServiceMapPath)
            ? opts.ServiceMapPath
            : Path.Combine(opts.BinaryRoot, "current", opts.ServiceMapPath);

        return _loader.LoadAsync(path, _components.Value.EnabledGroups(), cancellationToken);
    }
}

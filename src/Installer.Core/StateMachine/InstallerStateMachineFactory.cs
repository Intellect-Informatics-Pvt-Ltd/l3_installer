using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;
using SharedKernel.Contracts;

namespace Installer.Core.StateMachine;

/// <inheritdoc cref="IInstallerStateMachineFactory"/>
public sealed class InstallerStateMachineFactory : IInstallerStateMachineFactory
{
    private readonly IOptions<InstallerOptions> _options;
    private readonly ILogger<InstallerStateMachine> _logger;

    public InstallerStateMachineFactory(IOptions<InstallerOptions> options, ILogger<InstallerStateMachine> logger)
    {
        _options = options;
        _logger = logger;
    }

    public IInstallerStateMachine Create(InstallerMode mode, string targetVersion, string? previousVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetVersion);
        return new InstallerStateMachine(_options, _logger, mode, targetVersion, previousVersion);
    }
}

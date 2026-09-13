using SharedKernel.Contracts;

namespace Installer.Actions.Platform;

/// <summary>
/// Makes sure every account the service map names exists before a service is registered under
/// it. On a clean machine none of them do — and registration under a missing account fails on
/// Windows (<c>sc.exe obj=</c>, tasks.md X1) and produces a unit systemd refuses to start on
/// Linux (<c>User=</c> naming nobody). Either way the failure surfaces at the wrong step with a
/// message that names the symptom, not the cause.
/// </summary>
public interface IServiceAccountProvisioner
{
    /// <summary>Creates any missing account, as a system account with no login shell and no home. Returns the ones it created.</summary>
    Task<IReadOnlyList<string>> EnsureAsync(IReadOnlyList<ServiceMapEntry> services, CancellationToken cancellationToken = default);
}

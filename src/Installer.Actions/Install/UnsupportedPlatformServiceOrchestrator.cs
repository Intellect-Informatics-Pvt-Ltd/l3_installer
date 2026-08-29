using System.Runtime.InteropServices;
using SharedKernel.Contracts;

namespace Installer.Actions.Install;

/// <summary>
/// The <see cref="IServiceOrchestrator"/> registered on a platform that is neither Windows nor
/// Linux. Every method throws <see cref="PlatformNotSupportedException"/>.
///
/// WHY THIS EXISTS RATHER THAN A THROWING FACTORY. Failing at *resolve* time would make the whole
/// DI graph unbuildable on macOS, which takes two useful things away for no safety gain:
///
///   * <c>CompositionRootTests</c> could no longer validate the graph anywhere but on a target
///     platform — and the graph is exactly the thing that has already broken twice here.
///   * A developer could not dry-run the pipeline. Verification, prechecks, topology load and the
///     database plan are all platform-neutral and genuinely worth running on a laptop.
///
/// Failing at *use* keeps both, and still makes installing impossible. The dry run stops before
/// service registration by design, so it never reaches these methods; an <c>--apply</c> run
/// reaches them immediately and stops with a message naming the supported platforms.
/// </summary>
public sealed class UnsupportedPlatformServiceOrchestrator : IServiceOrchestrator
{
    private static PlatformNotSupportedException Unsupported(string operation) =>
        new($"Cannot {operation}: ePACS installs on Windows (sc.exe) and Debian/systemd only — see ADR-0010. " +
            $"This is {RuntimeInformation.OSDescription}. Everything up to service registration works here, so a " +
            "dry run is still useful; --apply is not.");

    public Task RegisterAllAsync(IReadOnlyList<ServiceMapEntry> services, CancellationToken cancellationToken = default) =>
        throw Unsupported("register services");

    public Task StartAllAsync(IReadOnlyList<ServiceMapEntry> services, CancellationToken cancellationToken = default) =>
        throw Unsupported("start services");

    public Task StopAllAsync(IReadOnlyList<ServiceMapEntry> services, CancellationToken cancellationToken = default) =>
        throw Unsupported("stop services");

    public Task DeregisterAllAsync(IReadOnlyList<ServiceMapEntry> services, CancellationToken cancellationToken = default) =>
        throw Unsupported("deregister services");
}

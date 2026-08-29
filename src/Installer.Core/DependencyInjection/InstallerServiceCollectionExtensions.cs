using Installer.Actions.Database;
using Installer.Actions.Install;
using Installer.Actions.Prechecks;
using Installer.Actions.Topology;
using Installer.Actions.Uninstall;
using Installer.Core.Pipeline;
using Installer.Core.Schema;
using Installer.Core.SiteConfig;
using Installer.Core.StateMachine;
using ManifestVerifier;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharedKernel.Configuration;
using SharedKernel.Security;

namespace Installer.Core.DependencyInjection;

/// <summary>
/// The composition root.
///
/// Everything the installer can do is registered here, in one place, so the question "what does
/// this product actually consist of?" has a file to point at. Before this existed,
/// <c>Installer.CLI</c> carried the comment <c>// TODO: Wire up full installer pipeline with
/// DI</c> and nine libraries sat unassembled.
/// </summary>
public static class InstallerServiceCollectionExtensions
{
    /// <summary>
    /// Registers every installer component and binds its configuration.
    /// </summary>
    public static IServiceCollection AddInstaller(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        AddOptions(services, configuration);
        AddVerification(services);
        AddPrechecks(services);
        AddInstallActions(services);
        AddStateMachine(services);

        services.AddSingleton<ISiteConfigLoader, SiteConfigLoader>();

        // Schema drift detection. Speaks the estate's own drift-key vocabulary and reads its
        // db/known-drift.txt, so an installer report and a verify-baseline-ddl.py report cannot
        // give a DBA two different answers about the same database.
        services.AddSingleton<ISchemaFingerprinter, MySqlSchemaFingerprinter>();
        services.AddSingleton<IInstallerPipeline, InstallerPipeline>();

        return services;
    }

    /// <summary>
    /// Options are bound with ValidateOnStart so a malformed appsettings fails at startup with
    /// the offending section named, rather than surfacing three phases in as a null path.
    /// </summary>
    private static void AddOptions(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<InstallerOptions>()
            .Bind(configuration.GetSection(InstallerOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<PrecheckOptions>()
            .Bind(configuration.GetSection(PrecheckOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<ServicesOptions>()
            .Bind(configuration.GetSection(ServicesOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<MonitoringOptions>()
            .Bind(configuration.GetSection(MonitoringOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<BackupOptions>()
            .Bind(configuration.GetSection(BackupOptions.SectionName))
            .ValidateOnStart();

        // Which components this installation includes. Eventing defaults to OFF: no deployment
        // in the L2-R2 estate provisions Kafka, and it is a ~290 MB payload with the JRE.
        services.AddOptions<ComponentsOptions>()
            .Bind(configuration.GetSection(ComponentsOptions.SectionName))
            .ValidateOnStart();
    }

    private static void AddVerification(IServiceCollection services)
    {
        services.AddSingleton<IManifestParser, ManifestParser>();
        services.AddSingleton<IHashVerifier, HashVerifier>();
        services.AddSingleton<ISignatureVerifier, SignatureVerifier>();
        services.AddSingleton<IManifestVerificationService, ManifestVerificationService>();
    }

    /// <summary>
    /// Registered as IPrecheck so PrecheckRunner receives all of them and orders by Order.
    /// Adding a check means adding one line here and nothing else — which is the point, but
    /// also the risk: a check that is written and not registered simply never runs, and its
    /// absence looks exactly like a pass. PrecheckRegistrationTests guards that.
    /// </summary>
    private static void AddPrechecks(IServiceCollection services)
    {
        services.AddSingleton<IPrecheck, OsVersionCheck>();
        services.AddSingleton<IPrecheck, DiskSpaceCheck>();
        services.AddSingleton<IPrecheck, RamCheck>();
        services.AddSingleton<IPrecheck, PortAvailabilityCheck>();
        services.AddSingleton<IPrecheck, AdminRightsCheck>();
        services.AddSingleton<IPrecheck, PendingRebootCheck>();
        services.AddSingleton<PrecheckRunner>();
    }

    private static void AddInstallActions(IServiceCollection services)
    {
        services.AddSingleton<IServiceMapLoader, ServiceMapLoader>();
        services.AddSingleton<IDataRootInitializer, DataRootInitializer>();
        services.AddSingleton<IPayloadExtractor, PayloadExtractor>();
        services.AddSingleton<IBinaryDeployer, BinaryDeployer>();
        services.AddSingleton<IConfigGenerator, ConfigGenerator>();
        // PLATFORM SELECTION — ADR-0010.
        //
        // The only place in the product that branches on the operating system. Everything above
        // IServiceOrchestrator works with a service map and does not know or care which service
        // manager will receive it; that is what keeps one pipeline serving both targets.
        //
        // It throws on an unsupported platform rather than defaulting to either. A default here
        // would mean a macOS developer's run silently exercising the Windows path and appearing
        // to work, which is precisely the class of "green on my machine" this repository has
        // already been bitten by twice.
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IServiceOrchestrator, ServiceOrchestrator>();
        }
        else if (OperatingSystem.IsLinux())
        {
            services.AddSingleton<IServiceOrchestrator, SystemdServiceOrchestrator>();
        }
        else
        {
            // Throws when USED, not when resolved — so the graph still validates and a dry run
            // still works on a developer's machine. See the type's own remarks.
            services.AddSingleton<IServiceOrchestrator, UnsupportedPlatformServiceOrchestrator>();
        }

        // The database bootstrap - the reason the framework exists. ProcessRunner is the only
        // seam that touches an external binary; everything that decides WHAT to run is pure.
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<ISecretStore, SecretStore>();
        services.AddSingleton<IDatabaseBootstrapper, MySqlBootstrapper>();

        // Uninstall's governance gate. IOverrideTokenValidator has no real implementation
        // (tasks.md 9.5), so the registered one REFUSES every token rather than accepting them:
        // a purge must be impossible until the validator is real, not accidentally permitted by
        // a stub that returns true.
        services.AddSingleton<IOverrideTokenValidator, DenyAllOverrideTokenValidator>();
        services.AddSingleton<UninstallAction>();
    }

    private static void AddStateMachine(IServiceCollection services)
    {
        // A factory, not the machine itself: the mode and target version are runtime values
        // (ModeDetector and the verified manifest), so the container cannot construct it. This
        // was caught by ValidateOnBuild the first time the CLI ran - see the factory's remarks.
        services.AddSingleton<IInstallerStateMachineFactory, InstallerStateMachineFactory>();
        services.AddSingleton<ModeDetector>();
        services.AddSingleton<InstallerLock>();
    }
}

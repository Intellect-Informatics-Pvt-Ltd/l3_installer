using FluentAssertions;
using Installer.Actions.Prechecks;
using Installer.Core.DependencyInjection;
using Installer.Core.Pipeline;
using Installer.Core.SiteConfig;
using Installer.Core.StateMachine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Installer.UnitTests;

/// <summary>
/// Guards the composition root.
///
/// The failure this prevents is quiet: a component that is written but never registered simply
/// never runs. For a precheck that is indistinguishable from a pass — the suite reports green
/// having skipped the check that would have stopped the install. There is no other test that
/// would notice.
/// </summary>
public sealed class CompositionRootTests
{
    private static ServiceProvider Build()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddInstaller(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    [Fact]
    public void The_container_builds_with_every_dependency_resolvable()
    {
        // ValidateOnBuild is what caught IInstallerStateMachine being registered as a singleton
        // when its constructor needs a runtime InstallerMode. Keep this test: the CLI validates
        // at startup, but only after a human has already invoked it on a real machine.
        var act = Build;

        act.Should().NotThrow();
    }

    [Fact]
    public void The_pipeline_resolves()
    {
        using var provider = Build();

        provider.GetRequiredService<IInstallerPipeline>().Should().NotBeNull();
    }

    [Fact]
    public void Every_precheck_that_exists_is_registered()
    {
        // Discovered by reflection rather than listed here: a check added to the assembly and
        // forgotten in AddPrechecks fails this test, which is the whole point.
        using var provider = Build();

        var registered = provider.GetServices<IPrecheck>().Select(p => p.GetType()).ToHashSet();
        var declared = typeof(PrecheckRunner).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IPrecheck).IsAssignableFrom(t))
            .ToList();

        declared.Should().NotBeEmpty();
        registered.Should().BeEquivalentTo(declared,
            "a precheck that is written but not registered never runs, and a check that never runs is indistinguishable from one that passed");
    }

    [Fact]
    public async Task The_override_token_validator_denies_by_default()
    {
        // The token authorises destroying a PACS node's business data. Until real validation
        // exists, the registered validator must refuse — a permissive stub would make an
        // irreversible operation available before its gate was written.
        using var provider = Build();
        var validator = provider.GetRequiredService<Installer.Actions.Uninstall.IOverrideTokenValidator>();

        var result = await validator.ValidateAsync("any-token-at-all", "purge");

        result.Valid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not implemented");
    }

    [Fact]
    public void The_site_config_loader_and_state_machine_factory_resolve()
    {
        using var provider = Build();

        provider.GetRequiredService<ISiteConfigLoader>().Should().NotBeNull();
        provider.GetRequiredService<IInstallerStateMachineFactory>().Should().NotBeNull();
    }
}

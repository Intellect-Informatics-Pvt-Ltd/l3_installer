using Harness.Common.Errors;
using Harness.Common.Observability;
using Harness.Common.Options;
using Harness.Common.TestHooks;
using Harness.Common.Time;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Harness.Common.Extensions;

/// <summary>
/// DI registration helpers shared by all harness projects.
/// Call <see cref="AddHarnessCommon"/> from each project's <c>Program.cs</c>.
/// </summary>
public static class HarnessCommonExtensions
{
    /// <summary>
    /// Registers all Harness.Common services: clock, error factory, fault
    /// injector (null when not in test mode), and typed options classes.
    /// </summary>
    public static IServiceCollection AddHarnessCommon(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Options — bind via Action<T> to avoid needing Microsoft.Extensions.Options.ConfigurationExtensions
        services.Configure<HarnessOptions>(o => configuration.GetSection(HarnessOptions.SectionName).Bind(o));
        services.Configure<SyncOptions>(o => configuration.GetSection(SyncOptions.SectionName).Bind(o));
        services.Configure<PacsOptions>(o => configuration.GetSection(PacsOptions.SectionName).Bind(o));
        services.Configure<NldrOptions>(o => configuration.GetSection(NldrOptions.SectionName).Bind(o));
        services.Configure<UiOptions>(o => configuration.GetSection(UiOptions.SectionName).Bind(o));

        // Clock — can be overridden in tests by replacing with OffsetClock
        services.AddSingleton<IClock, SystemClock>();

        // Error factory
        services.AddSingleton<IErrorFactory, DefaultErrorFactory>();

        // Generic app-logger registration
        services.AddTransient(typeof(IAppLogger<>), typeof(DefaultAppLogger<>));

        // Fault injector — null unless TestMode is true (wired up per project)
        services.AddSingleton<IFaultInjector>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<HarnessOptions>>().Value;
            if (!opts.TestMode) return NullFaultInjector.Instance;
            return (IFaultInjector?)sp.GetService<RedisFaultInjector>() ?? NullFaultInjector.Instance;
        });

        return services;
    }
}

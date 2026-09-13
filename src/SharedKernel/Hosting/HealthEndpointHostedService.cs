using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SharedKernel.Hosting;

/// <summary>
/// Hosts a <see cref="HealthEndpoint"/> for the lifetime of a worker. Registered FIRST so
/// <c>/health/live</c> answers before the worker's own loop starts, and the readiness flag is
/// flipped by the worker once it has done its own start-up.
/// </summary>
public sealed class HealthEndpointHostedService : IHostedService, IAsyncDisposable
{
    private readonly HealthEndpoint _endpoint;

    public HealthEndpointHostedService(HealthEndpoint endpoint) => _endpoint = endpoint;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _endpoint.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync() => _endpoint.DisposeAsync();
}

public static class HealthEndpointServiceCollectionExtensions
{
    /// <summary>Answer <c>/health/live</c> and <c>/health/ready</c> on <paramref name="port"/>, loopback only.</summary>
    public static IServiceCollection AddHealthEndpoint(this IServiceCollection services, int port)
    {
        services.AddSingleton(sp => new HealthEndpoint(port, sp.GetRequiredService<ILoggerFactory>().CreateLogger<HealthEndpoint>()));
        services.AddHostedService<HealthEndpointHostedService>();
        return services;
    }
}

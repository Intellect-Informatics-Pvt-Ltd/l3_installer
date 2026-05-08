using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;
using Sync.Abstractions;

namespace Sync.Agent.Connectivity;

/// <summary>
/// Monitors NLDR connectivity using periodic probes.
/// Manages the circuit breaker state machine.
/// When connected: sync proceeds. When open: business operations unaffected, sync queued.
/// </summary>
public sealed class ConnectivityMonitor
{
    private readonly ISyncTransport _transport;
    private readonly ConnectivityState _state;
    private readonly IOptions<ServicesOptions> _servicesOptions;
    private readonly ILogger<ConnectivityMonitor> _logger;

    public ConnectivityMonitor(
        ISyncTransport transport,
        ConnectivityState state,
        IOptions<ServicesOptions> servicesOptions,
        ILogger<ConnectivityMonitor> logger)
    {
        _transport = transport;
        _state = state;
        _servicesOptions = servicesOptions;
        _logger = logger;
    }

    /// <summary>Current connectivity state.</summary>
    public ConnectivityState State => _state;

    /// <summary>
    /// Probes NLDR connectivity and updates the state machine.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if NLDR is reachable.</returns>
    public async Task<bool> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (!_transport.Enabled)
        {
            _logger.LogInformation("Sync transport is disabled. Connectivity probe skipped.");
            return false;
        }

        // Only probe if state machine says we should
        if (_state.Status == ConnectionStatus.Connected)
        {
            // Already connected — periodic verification
        }
        else if (!_state.ShouldProbe())
        {
            _logger.LogInformation("Circuit breaker OPEN. Waiting for cooldown before next probe.");
            return false;
        }

        try
        {
            var reachable = await _transport.ProbeConnectivityAsync(cancellationToken);

            if (reachable)
            {
                _state.RecordSuccess();
                _logger.LogInformation("NLDR connectivity probe: SUCCESS. Status: Connected.");
                return true;
            }

            _state.RecordFailure();
            _logger.LogWarning("NLDR connectivity probe: FAILED (endpoint returned negative).");
            return false;
        }
        catch (Exception ex)
        {
            _state.RecordFailure();
            _logger.LogWarning(ex, "NLDR connectivity probe: FAILED with exception.");
            return false;
        }
    }

    /// <summary>
    /// Detects approximate bandwidth based on probe response time.
    /// Used to adjust chunk sizes for file sync.
    /// </summary>
    /// <param name="probeResponseMs">Response time of the last probe in milliseconds.</param>
    /// <returns>Estimated bandwidth tier.</returns>
    public static BandwidthTier DetectBandwidth(double probeResponseMs) => probeResponseMs switch
    {
        < 200 => BandwidthTier.High4G,
        < 1000 => BandwidthTier.Medium3G,
        _ => BandwidthTier.Low2G
    };
}

/// <summary>
/// Bandwidth tier for adaptive chunk sizing.
/// </summary>
public enum BandwidthTier
{
    /// <summary>4G — chunk size 1 MB.</summary>
    High4G,

    /// <summary>3G — chunk size 256 KB.</summary>
    Medium3G,

    /// <summary>2G — chunk size 64 KB.</summary>
    Low2G
}

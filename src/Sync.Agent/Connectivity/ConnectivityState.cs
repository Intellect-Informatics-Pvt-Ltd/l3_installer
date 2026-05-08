namespace Sync.Agent.Connectivity;

/// <summary>
/// Connectivity state machine for NLDR communication.
/// States: Connected → Open (disconnected) → HalfOpen (probing).
/// Circuit breaker pattern with configurable thresholds.
/// </summary>
public sealed class ConnectivityState
{
    private readonly int _failureThreshold;
    private readonly TimeSpan _cooldownPeriod;
    private readonly object _lock = new();

    private ConnectionStatus _status = ConnectionStatus.Unknown;
    private int _consecutiveFailures;
    private DateTimeOffset _lastFailureAt;
    private DateTimeOffset _lastSuccessAt;
    private DateTimeOffset _onlineSince;

    public ConnectivityState(int failureThreshold = 5, int cooldownSeconds = 300)
    {
        _failureThreshold = failureThreshold;
        _cooldownPeriod = TimeSpan.FromSeconds(cooldownSeconds);
    }

    /// <summary>Current connection status.</summary>
    public ConnectionStatus Status
    {
        get { lock (_lock) { return _status; } }
    }

    /// <summary>Whether sync operations should proceed.</summary>
    public bool CanSync => Status == ConnectionStatus.Connected;

    /// <summary>When the PACS came online (for heartbeat payload).</summary>
    public DateTimeOffset OnlineSince => _onlineSince;

    /// <summary>Last successful communication timestamp.</summary>
    public DateTimeOffset LastSuccessAt => _lastSuccessAt;

    /// <summary>
    /// Records a successful communication with NLDR.
    /// </summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            if (_status != ConnectionStatus.Connected)
            {
                _onlineSince = DateTimeOffset.UtcNow;
            }
            _status = ConnectionStatus.Connected;
            _consecutiveFailures = 0;
            _lastSuccessAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Records a failed communication attempt.
    /// </summary>
    public void RecordFailure()
    {
        lock (_lock)
        {
            _consecutiveFailures++;
            _lastFailureAt = DateTimeOffset.UtcNow;

            if (_consecutiveFailures >= _failureThreshold)
            {
                _status = ConnectionStatus.Open; // Circuit breaker open
            }
        }
    }

    /// <summary>
    /// Checks if the circuit breaker should transition to half-open (ready to probe).
    /// </summary>
    public bool ShouldProbe()
    {
        lock (_lock)
        {
            if (_status != ConnectionStatus.Open)
            {
                return _status == ConnectionStatus.Unknown; // Probe on first run
            }

            // Transition to half-open after cooldown
            if (DateTimeOffset.UtcNow - _lastFailureAt > _cooldownPeriod)
            {
                _status = ConnectionStatus.HalfOpen;
                return true;
            }

            return false;
        }
    }
}

/// <summary>
/// NLDR connection status.
/// </summary>
public enum ConnectionStatus
{
    /// <summary>Initial state — not yet probed.</summary>
    Unknown,

    /// <summary>NLDR is reachable — sync operations proceed.</summary>
    Connected,

    /// <summary>Circuit breaker open — too many failures, waiting for cooldown.</summary>
    Open,

    /// <summary>Probing — one test request allowed to check if NLDR is back.</summary>
    HalfOpen
}

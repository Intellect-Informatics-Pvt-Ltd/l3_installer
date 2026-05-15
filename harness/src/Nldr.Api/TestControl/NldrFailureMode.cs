namespace Nldr.Api.TestControl;

/// <summary>
/// NLDR test failure modes (§13.2 — all 8 modes).
/// Stored in a singleton and checked at ingest pipeline step 4.
/// </summary>
public enum NldrMode
{
    Healthy,
    Http500,
    Timeout,
    DropAck,
    RateLimit,
    BadAck,
    HashStrict,
    SequenceStrict
}

/// <summary>
/// In-process mutable state for the NLDR TestControl.
/// Thread-safe via Interlocked / volatile.
/// </summary>
public sealed class NldrTestState
{
    private volatile NldrMode _mode = NldrMode.Healthy;
    private volatile int      _count;
    private volatile int      _retryAfterSec = 20;
    private volatile int      _delayMs = 5000;

    public NldrMode Mode            { get => _mode;         set => _mode = value; }
    public int      Count           { get => _count;        set => _count = value; }
    public int      RetryAfterSec   { get => _retryAfterSec; set => _retryAfterSec = value; }
    public int      DelayMs         { get => _delayMs;      set => _delayMs = value; }

    /// <summary>
    /// Consumes one count. If Count &gt; 0 and reaches 0 after decrement, resets to Healthy.
    /// Returns the mode that was active before any potential reset.
    /// </summary>
    public NldrMode ConsumeAndMaybeReset()
    {
        var current = _mode;
        if (current == NldrMode.Healthy) return current;

        if (_count > 0)
        {
            var remaining = Interlocked.Decrement(ref _count);
            if (remaining <= 0) _mode = NldrMode.Healthy;
        }
        return current;
    }

    public void Reset()
    {
        _mode  = NldrMode.Healthy;
        _count = 0;
    }
}

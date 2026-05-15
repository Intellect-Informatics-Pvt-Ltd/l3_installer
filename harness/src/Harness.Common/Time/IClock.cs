namespace Harness.Common.Time;

/// <summary>
/// Abstraction over the system clock. All components use this instead of
/// <c>DateTime.UtcNow</c> / <c>DateTimeOffset.UtcNow</c> so that
/// <see cref="OffsetClock"/> can simulate time travel in tests (§13.4).
/// </summary>
public interface IClock
{
    /// <summary>Current UTC time (potentially offset for testing).</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>Real system clock — used in production.</summary>
public sealed class SystemClock : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// Clock implementation that adds a configurable offset to the system time.
/// The offset is persisted in Redis under <c>pacs:clock-offset-seconds</c>
/// so all processes in the same PACS profile see the same offset.
/// </summary>
public sealed class OffsetClock : IClock
{
    private TimeSpan _offset = TimeSpan.Zero;

    /// <summary>Current offset applied on top of the system clock.</summary>
    public TimeSpan Offset
    {
        get => _offset;
        set => _offset = value;
    }

    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow + _offset;

    /// <summary>Resets the offset to zero.</summary>
    public void Reset() => _offset = TimeSpan.Zero;

    /// <summary>Advances the offset by <paramref name="seconds"/> seconds.</summary>
    public void Jump(int seconds) => _offset = _offset.Add(TimeSpan.FromSeconds(seconds));
}

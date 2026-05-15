namespace Harness.Common.TestHooks;

/// <summary>
/// Named instrumentation points where fault injection can be armed (§13.3).
/// Every hosted service and API handler fires the relevant hook via
/// <see cref="IFaultInjector.FireAsync"/> before/after each significant action.
/// </summary>
public enum FaultHook
{
    /// <summary>Fired inside the business transaction just before COMMIT.</summary>
    BeforeDbCommit,

    /// <summary>Fired immediately after COMMIT returns successfully.</summary>
    AfterDbCommit,

    /// <summary>Fired by OutboundRelayService before <c>IKafkaProducer.PublishAsync</c>.</summary>
    BeforeKafkaPublish,

    /// <summary>Fired after <c>IKafkaProducer.PublishAsync</c> returns.</summary>
    AfterKafkaPublish,

    /// <summary>Fired in the ACK handler before the outbox row is set to ACKED.</summary>
    BeforeAckUpdate,

    /// <summary>Fired after the ACK row has been persisted.</summary>
    AfterAckUpdate,

    /// <summary>Fired in the NLDR ingest pipeline before the business write.</summary>
    BeforeInboxApply,

    /// <summary>Fired after the NLDR business write commits.</summary>
    AfterInboxApply,

    /// <summary>Fired before the relay service SELECTs rows FOR UPDATE.</summary>
    BeforeOutboxFetch,

    /// <summary>Fired after a row is flipped to IN_FLIGHT.</summary>
    AfterMarkInFlight,

    /// <summary>Fired before each file chunk is uploaded.</summary>
    BeforeFileChunkUpload,

    /// <summary>Fired after each chunk ACK is received.</summary>
    AfterFileChunkAck,

    /// <summary>Fired before the heartbeat is published to Kafka.</summary>
    BeforeHeartbeatPublish
}

/// <summary>Modes available when arming a fault hook.</summary>
public enum FaultHookMode
{
    /// <summary>Record the visit but do nothing (default).</summary>
    Noop,

    /// <summary>Block execution until <c>DurationMs</c> elapses or the hook is released.</summary>
    Pause,

    /// <summary>Call <c>Environment.Exit(1)</c> to simulate a hard process kill.</summary>
    Crash,

    /// <summary>Throw the configured exception type, exercising the retry path.</summary>
    Throw
}

/// <summary>Configuration for an armed fault hook.</summary>
public sealed record FaultHookConfig
{
    public FaultHookMode Mode    { get; init; } = FaultHookMode.Noop;
    public int           Count   { get; init; } = 1;
    public int?          DurationMs { get; init; }
    public string?       ExceptionTypeName { get; init; }
}

/// <summary>
/// Contract for the fault-injection subsystem. Implementations store the armed
/// hooks in Redis (<c>pacs:fault:*</c>) so all in-process services see the same
/// state.
/// </summary>
public interface IFaultInjector
{
    /// <summary>
    /// Fires the hook if it has been armed. Does nothing when the hook is not
    /// armed or <c>Harness:TestMode = false</c>.
    /// </summary>
    Task FireAsync(FaultHook hook, CancellationToken ct = default);

    /// <summary>Arms a hook with the given configuration.</summary>
    Task ArmAsync(FaultHook hook, FaultHookConfig config, CancellationToken ct = default);

    /// <summary>Disarms all hooks.</summary>
    Task ClearAllAsync(CancellationToken ct = default);

    /// <summary>Returns all currently armed hooks with remaining counts.</summary>
    Task<IReadOnlyDictionary<FaultHook, FaultHookConfig>> GetStateAsync(CancellationToken ct = default);
}

/// <summary>
/// No-op implementation used when <c>Harness:TestMode = false</c>.
/// </summary>
public sealed class NullFaultInjector : IFaultInjector
{
    public static readonly NullFaultInjector Instance = new();

    public Task FireAsync(FaultHook hook, CancellationToken ct = default) => Task.CompletedTask;
    public Task ArmAsync(FaultHook hook, FaultHookConfig config, CancellationToken ct = default) => Task.CompletedTask;
    public Task ClearAllAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyDictionary<FaultHook, FaultHookConfig>> GetStateAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<FaultHook, FaultHookConfig>>(
            new Dictionary<FaultHook, FaultHookConfig>());
}

using Harness.Common.Canonicalization;
using Harness.Common.Identifiers;
using Harness.Common.Time;

namespace Harness.Common.Envelope;

/// <summary>
/// Fluent builder for <see cref="EventEnvelope"/>. Computes
/// <see cref="EventEnvelope.PayloadHash"/> automatically via
/// <see cref="PayloadHasher"/> so callers never compute it manually.
/// </summary>
public sealed class EventEnvelopeBuilder
{
    private string _eventId = EventIdProvider.NewEventId();
    private string _correlationId = string.Empty;
    private string? _causationId;
    private string _pacsId = string.Empty;
    private string _streamName = "pacs.outbound";
    private long _sequenceNo;
    private string _idempotencyKey = string.Empty;
    private ChangeType _changeType;
    private string _entityType = string.Empty;
    private string _entityId = string.Empty;
    private object? _payload;
    private object? _beforeState;
    private AmendmentMeta? _amendmentMeta;
    private DateTimeOffset _createdAtUtc;

    public EventEnvelopeBuilder WithEventId(string eventId)   { _eventId = eventId;   return this; }
    public EventEnvelopeBuilder WithCorrelation(string id)    { _correlationId = id;  return this; }
    public EventEnvelopeBuilder WithCausation(string id)      { _causationId = id;    return this; }
    public EventEnvelopeBuilder WithPacsId(string id)         { _pacsId = id;         return this; }
    public EventEnvelopeBuilder WithStream(string name)       { _streamName = name;   return this; }
    public EventEnvelopeBuilder WithSequenceNo(long no)       { _sequenceNo = no;     return this; }
    public EventEnvelopeBuilder WithIdempotencyKey(string key){ _idempotencyKey = key; return this; }
    public EventEnvelopeBuilder WithChangeType(ChangeType t)  { _changeType = t;      return this; }
    public EventEnvelopeBuilder WithEntityType(string t)      { _entityType = t;      return this; }
    public EventEnvelopeBuilder WithEntityId(string id)       { _entityId = id;       return this; }
    public EventEnvelopeBuilder WithPayload(object? p)        { _payload = p;         return this; }
    public EventEnvelopeBuilder WithBeforeState(object? bs)   { _beforeState = bs;    return this; }
    public EventEnvelopeBuilder WithAmendmentMeta(AmendmentMeta? m) { _amendmentMeta = m; return this; }
    public EventEnvelopeBuilder WithCreatedAt(DateTimeOffset at)    { _createdAtUtc = at; return this; }

    /// <summary>
    /// Builds the envelope. <see cref="EventEnvelope.PayloadHash"/> is computed
    /// here — this is the only place in the solution that calls
    /// <see cref="PayloadHasher"/>.
    /// </summary>
    public EventEnvelope Build(IClock? clock = null)
    {
        var createdAt = _createdAtUtc == default
            ? (clock?.UtcNow ?? DateTimeOffset.UtcNow)
            : _createdAtUtc;

        var hash = PayloadHasher.Compute(_payload, _beforeState, _amendmentMeta);

        return new EventEnvelope
        {
            EventId          = _eventId,
            CorrelationId    = _correlationId,
            CausationId      = _causationId,
            PacsId           = _pacsId,
            StreamName       = _streamName,
            SequenceNo       = _sequenceNo,
            IdempotencyKey   = _idempotencyKey,
            ChangeType       = _changeType,
            EntityType       = _entityType,
            EntityId         = _entityId,
            Payload          = _payload,
            BeforeState      = _beforeState,
            AmendmentMeta    = _amendmentMeta,
            PayloadHash      = hash,
            CreatedAtUtc     = createdAt
        };
    }
}

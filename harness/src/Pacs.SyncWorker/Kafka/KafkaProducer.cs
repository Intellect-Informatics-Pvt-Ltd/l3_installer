using Confluent.Kafka;
using Harness.Common.Envelope;
using Harness.Common.Options;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Pacs.SyncWorker.Kafka;

/// <summary>
/// Publishes <see cref="EventEnvelope"/> instances to a Kafka topic.
/// Wraps Confluent.Kafka with a strongly typed interface; in a full build
/// this would use <c>Intellect.Erp.Messaging.Kafka.IKafkaProducer</c>.
/// </summary>
public interface IKafkaEnvelopeProducer : IAsyncDisposable
{
    Task PublishAsync(string topic, EventEnvelope envelope, CancellationToken ct = default);
}

public sealed class KafkaEnvelopeProducer : IKafkaEnvelopeProducer
{
    private readonly IProducer<string, string> _producer;
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    public KafkaEnvelopeProducer(IOptions<MessagingOptions> opts)
    {
        var config = new ProducerConfig
        {
            BootstrapServers  = opts.Value.Kafka.BootstrapServers,
            EnableIdempotence = true,
            Acks              = Acks.All,
            MessageSendMaxRetries = 5
        };
        _producer = new ProducerBuilder<string, string>(config).Build();
    }

    public async Task PublishAsync(string topic, EventEnvelope envelope, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(envelope, _json);
        var message = new Message<string, string>
        {
            Key   = envelope.PacsId,
            Value = payload,
            Headers = new Headers
            {
                { "correlationId",  System.Text.Encoding.UTF8.GetBytes(envelope.CorrelationId) },
                { "eventId",        System.Text.Encoding.UTF8.GetBytes(envelope.EventId) },
                { "pacsId",         System.Text.Encoding.UTF8.GetBytes(envelope.PacsId) },
                { "sequenceNo",     System.Text.Encoding.UTF8.GetBytes(envelope.SequenceNo.ToString(System.Globalization.CultureInfo.InvariantCulture)) },
                { "idempotencyKey", System.Text.Encoding.UTF8.GetBytes(envelope.IdempotencyKey) },
                { "eventType",      System.Text.Encoding.UTF8.GetBytes($"epacs.{envelope.EntityType}.{envelope.ChangeType}") },
                { "schemaVersion",  System.Text.Encoding.UTF8.GetBytes(envelope.SchemaVersion) }
            }
        };
        await _producer.ProduceAsync(topic, message, ct);
    }

    public ValueTask DisposeAsync()
    {
        _producer.Flush(TimeSpan.FromSeconds(10));
        _producer.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Kafka configuration options (subset).</summary>
public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";
    public KafkaOptions Kafka { get; set; } = new();
}

public sealed class KafkaOptions
{
    public string BootstrapServers { get; set; } = "localhost:9092";
}

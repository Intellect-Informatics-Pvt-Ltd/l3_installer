using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Nldr.SyncWorker.Workers;

namespace Nldr.SyncWorker.Kafka;

/// <summary>Raw Kafka publisher used by <see cref="AckPublisherService"/>.</summary>
public interface INldrKafkaProducer : IAsyncDisposable
{
    Task PublishRawAsync(string topic, string key, string value, CancellationToken ct = default);
}

public sealed class NldrKafkaProducer : INldrKafkaProducer
{
    private readonly IProducer<string, string> _producer;

    public NldrKafkaProducer(IOptions<NldrKafkaOptions> opts)
    {
        var config = new ProducerConfig
        {
            BootstrapServers   = opts.Value.BootstrapServers,
            EnableIdempotence  = true,
            Acks               = Acks.All
        };
        _producer = new ProducerBuilder<string, string>(config).Build();
    }

    public async Task PublishRawAsync(string topic, string key, string value, CancellationToken ct = default)
    {
        await _producer.ProduceAsync(topic, new Message<string, string> { Key = key, Value = value }, ct);
    }

    public ValueTask DisposeAsync()
    {
        _producer.Flush(TimeSpan.FromSeconds(10));
        _producer.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class NldrKafkaOptions
{
    public const string SectionName = "Messaging:Kafka";
    public string BootstrapServers { get; set; } = "localhost:9092";
}

using DotNet.Testcontainers.Builders;
using Xunit;
using Testcontainers.Kafka;
using Testcontainers.MySql;
using Testcontainers.Redis;

namespace Harness.IntegrationTests.Infrastructure;

/// <summary>
/// Manages Testcontainers infrastructure (MySQL × 2, Kafka, Redis × 2) for
/// the full M1 happy-path integration tests.
/// Implements <see cref="IAsyncLifetime"/> so xUnit creates/destroys containers
/// once per class.
/// </summary>
public sealed class HarnessInfrastructure : IAsyncLifetime
{
    // ── MySQL containers ─────────────────────────────────────────────────────
    public MySqlContainer PacsMySql { get; } = new MySqlBuilder()
        .WithDatabase("epacs_pacs")
        .WithUsername("root")
        .WithPassword("root")
        .WithImage("mysql:8.4")
        .WithCommand("--innodb_flush_log_at_trx_commit=1", "--character-set-server=utf8mb4")
        .Build();

    public MySqlContainer NldrMySql { get; } = new MySqlBuilder()
        .WithDatabase("epacs_nldr")
        .WithUsername("root")
        .WithPassword("root")
        .WithImage("mysql:8.4")
        .WithCommand("--innodb_flush_log_at_trx_commit=1", "--character-set-server=utf8mb4")
        .Build();

    // ── Kafka ─────────────────────────────────────────────────────────────────
    public KafkaContainer Kafka { get; } = new KafkaBuilder()
        .WithImage("confluentinc/cp-kafka:7.6.0")
        .Build();

    // ── Redis containers ─────────────────────────────────────────────────────
    public RedisContainer PacsRedis { get; } = new RedisBuilder().Build();
    public RedisContainer NldrRedis { get; } = new RedisBuilder().Build();

    // ── Connection strings ────────────────────────────────────────────────────
    public string PacsConnStr => PacsMySql.GetConnectionString();
    public string NldrConnStr => NldrMySql.GetConnectionString();
    public string KafkaBootstrap => Kafka.GetBootstrapAddress();
    public string PacsRedisConn => $"{PacsRedis.Hostname}:{PacsRedis.GetMappedPublicPort(6379)},abortConnect=false";
    public string NldrRedisConn => $"{NldrRedis.Hostname}:{NldrRedis.GetMappedPublicPort(6379)},abortConnect=false";

    public async Task InitializeAsync()
    {
        // Start all containers in parallel
        await Task.WhenAll(
            PacsMySql.StartAsync(),
            NldrMySql.StartAsync(),
            Kafka.StartAsync(),
            PacsRedis.StartAsync(),
            NldrRedis.StartAsync());

        // Apply migrations
        await MigratorHelper.ApplyPacsMigrationsAsync(PacsConnStr);
        await MigratorHelper.ApplyNldrMigrationsAsync(NldrConnStr);
    }

    public async Task DisposeAsync()
    {
        await Task.WhenAll(
            PacsMySql.DisposeAsync().AsTask(),
            NldrMySql.DisposeAsync().AsTask(),
            Kafka.DisposeAsync().AsTask(),
            PacsRedis.DisposeAsync().AsTask(),
            NldrRedis.DisposeAsync().AsTask());
    }
}

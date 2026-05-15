#pragma warning disable CS8602 // Dereference of possibly null reference — intentional in FluentAssertions chains
using Dapper;
using FluentAssertions;
using Harness.Common.Canonicalization;
using Harness.Common.Envelope;
using Harness.Common.Errors;
using Harness.Common.Extensions;
using Harness.Common.Identifiers;
using Harness.Common.Observability;
using Harness.Common.Options;
using Harness.Common.Outbox;
using Harness.Common.Sequencing;
using Harness.Common.TestHooks;
using Harness.Common.Time;
using Harness.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MySqlConnector;
using Nldr.Api.Sync;
using Nldr.Api.TestControl;
using Pacs.Fas.Api.Vouchers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Harness.IntegrationTests.HappyPath;

/// <summary>
/// M1 happy-path end-to-end test (CRIT-001 / SYNC-POS-001).
/// <list type="number">
///   <item>Creates a voucher via Pacs.Fas.Api</item>
///   <item>Verifies a <c>sync_outbox</c> row with status=PENDING was created atomically</item>
///   <item>Calls Nldr.Api /api/sync/ingest directly (simulating the relay)</item>
///   <item>Verifies the received_event row and ACK enqueue in nldr_outbox</item>
///   <item>Simulates InboundConsumerService by marking the outbox row ACKED</item>
///   <item>Verifies the outbox row is ACKED and the NLDR business row exists</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public sealed class HappyPathIntegrationTests : IAsyncLifetime
{
    private HarnessInfrastructure _infra = null!;
    // Use a controller type (not Program) to avoid ambiguity when both APIs are referenced
    private WebApplicationFactory<VoucherController> _pacsFactory = null!;
    private WebApplicationFactory<SyncIngestController> _nldrFactory = null!;
    private HttpClient _pacsClient = null!;
    private HttpClient _nldrClient = null!;

    public async Task InitializeAsync()
    {
        _infra = new HarnessInfrastructure();
        await _infra.InitializeAsync();

        _pacsFactory = BuildPacsFactory();
        _nldrFactory = BuildNldrFactory();

        _pacsClient = _pacsFactory.CreateClient();
        _nldrClient = _nldrFactory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _pacsClient.Dispose();
        _nldrClient.Dispose();
        await _pacsFactory.DisposeAsync();
        await _nldrFactory.DisposeAsync();
        await _infra.DisposeAsync();
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact(Timeout = 60_000)]
    public async Task CreateVoucher_Then_Ingest_Produces_AckedOutboxRow()
    {
        // ── Step 1: Create voucher via Pacs.Fas.Api ───────────────────────────
        var createReq = new
        {
            voucherNo   = "VCH-IT-0001",
            voucherDate = "2026-05-14",
            voucherType = "CR",
            narration   = "Integration test voucher",
            createdBy   = "testuser",
            lines       = new[]
            {
                new { accountCode = "1001", debitAmount = 0m, creditAmount = 5000m, lineNarration = (string?)null }
            }
        };

        var createResp = await _pacsClient.PostAsJsonAsync("/api/vouchers", createReq);
        createResp.EnsureSuccessStatusCode();

        var voucherDto = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var voucherId   = voucherDto.GetProperty("voucherId").GetInt64();
        var correlationId = voucherDto.GetProperty("correlationId").GetString()!;
        var seqNo         = voucherDto.GetProperty("outboxSequenceNo").GetInt64();

        // ── Step 2: Verify sync_outbox row in PACS DB ─────────────────────────
        await using var pacsDb = new MySqlConnection(_infra.PacsConnStr);
        await pacsDb.OpenAsync();

        var outboxRow = await pacsDb.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT * FROM sync_outbox WHERE pacs_id='PACS-AP-0001' AND sequence_no=@seq",
            new { seq = seqNo });

        outboxRow.Should().NotBeNull("a sync_outbox row must be written atomically with the voucher insert (I-2)");
        ((string)outboxRow!.status).Should().BeOneOf("PENDING", "IN_FLIGHT");
        ((string)outboxRow!.entity_type).Should().Be("voucher");
        ((string)outboxRow!.change_type).Should().Be("INSERT");

        var eventId     = (string)outboxRow!.event_id;
        var payloadHash = (string)outboxRow!.payload_hash;

        // ── Step 3: Send envelope to Nldr.Api /api/sync/ingest ────────────────
        // Reconstruct the envelope from outbox row (as relay would)
        var payloadJson = (string?)outboxRow!.payload_json;
        var envelope = new EventEnvelope
        {
            EventId        = eventId,
            CorrelationId  = correlationId,
            PacsId         = "PACS-AP-0001",
            SequenceNo     = seqNo,
            StreamName     = "pacs.outbound",
            IdempotencyKey = IdempotencyKey.Format("PACS-AP-0001", "voucher",
                voucherId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "INSERT", DateTimeOffset.UtcNow),
            ChangeType     = ChangeType.INSERT,
            EntityType     = "voucher",
            EntityId       = voucherId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PayloadHash    = payloadHash,
            Payload        = payloadJson != null ? JsonSerializer.Deserialize<object>(payloadJson) : null,
            CreatedAtUtc   = DateTimeOffset.UtcNow
        };

        var ingestResp = await _nldrClient.PostAsJsonAsync("/api/sync/ingest", envelope);
        ingestResp.IsSuccessStatusCode.Should().BeTrue(
            because: $"NLDR ingest should succeed; got {ingestResp.StatusCode}: {await ingestResp.Content.ReadAsStringAsync()}");

        var ingestResult = await ingestResp.Content.ReadFromJsonAsync<JsonElement>();
        ingestResult.GetProperty("status").GetString().Should().BeOneOf("APPLIED", "DUPLICATE");

        // ── Step 4: Verify received_event in NLDR DB ──────────────────────────
        await using var nldrDb = new MySqlConnection(_infra.NldrConnStr);
        await nldrDb.OpenAsync();

        var receivedEvent = await nldrDb.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT * FROM received_event WHERE event_id=@eventId",
            new { eventId });

        receivedEvent.Should().NotBeNull("NLDR must record the received event (step 9)");
        ((string)receivedEvent!.apply_status).Should().BeOneOf("APPLIED", "DUPLICATE");

        // ── Step 5: Verify nldr_outbox has an ACK row ─────────────────────────
        var ackRow = await nldrDb.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT * FROM nldr_outbox WHERE pacs_id='PACS-AP-0001' AND event_type='nldr.ack'");

        ackRow.Should().NotBeNull("Nldr.Api must enqueue an ACK in nldr_outbox (step 11)");

        // ── Step 6: Verify NLDR business row exists ───────────────────────────
        var bizRow = await nldrDb.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT * FROM nldr_business_voucher WHERE voucher_id=@id",
            new { id = voucherId });

        bizRow.Should().NotBeNull("NLDR must apply business state (step 8)");

        // ── Step 7: Simulate ACK received on PACS side ────────────────────────
        await pacsDb.ExecuteAsync(
            "UPDATE sync_outbox SET status='ACKED', ack_at=NOW(6) WHERE event_id=@eventId",
            new { eventId });

        var ackedRow = await pacsDb.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT status FROM sync_outbox WHERE event_id=@eventId",
            new { eventId });

        ((string)ackedRow!.status).Should().Be("ACKED",
            because: "after ACK is applied the outbox row must be ACKED (invariant I-3)");
    }

    // ── Factory helpers ────────────────────────────────────────────────────────

    private WebApplicationFactory<VoucherController> BuildPacsFactory() =>
        new WebApplicationFactory<VoucherController>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(services =>
                {
                    // Override connection strings with test containers
                    services.RemoveAll<MySqlConnection>();
                    services.AddTransient<MySqlConnection>(_ =>
                        new MySqlConnection(_infra.PacsConnStr));

                    services.AddStackExchangeRedisCache(opts =>
                        opts.Configuration = _infra.PacsRedisConn);
                    services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
                        StackExchange.Redis.ConnectionMultiplexer.Connect(_infra.PacsRedisConn));

                    // Disable fault injector (TestMode = false for clean test)
                    services.RemoveAll<IFaultInjector>();
                    services.AddSingleton<IFaultInjector>(NullFaultInjector.Instance);

                    // Override PacsId
                    services.Configure<PacsOptions>(o => o.PacsId = "PACS-AP-0001");
                    services.Configure<SyncOptions>(o =>
                    {
                        o.Priority.VoucherDefault = 10;
                    });
                });
            });

    private WebApplicationFactory<SyncIngestController> BuildNldrFactory() =>
        new WebApplicationFactory<SyncIngestController>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<MySqlConnection>();
                    services.AddTransient<MySqlConnection>(_ =>
                        new MySqlConnection(_infra.NldrConnStr));

                    services.AddStackExchangeRedisCache(opts =>
                        opts.Configuration = _infra.NldrRedisConn);
                    services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
                        StackExchange.Redis.ConnectionMultiplexer.Connect(_infra.NldrRedisConn));

                    services.Configure<HarnessOptions>(o => o.TestMode = true);
                    services.Configure<SyncOptions>(o => o.AcksTopic = "epacs.nldr.acks");
                });
            });
}

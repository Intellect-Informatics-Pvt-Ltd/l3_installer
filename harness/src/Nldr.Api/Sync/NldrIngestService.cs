using Harness.Common.Canonicalization;
using Harness.Common.Envelope;
using Harness.Common.Errors;
using Harness.Common.Inbox;
using Harness.Common.Observability;
using Harness.Common.Options;
using Harness.Common.TestHooks;
using Harness.Common.Time;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Nldr.Api.TestControl;
using System.Text.Json;

namespace Nldr.Api.Sync;

public interface INldrIngestService
{
    Task<IngestResponse> IngestAsync(EventEnvelope envelope, string testToken, CancellationToken ct = default);
}

public sealed class NldrIngestService(
    MySqlConnection db,
    INldrIngestRepository repo,
    NldrTestState testState,
    IOptions<HarnessOptions> harness,
    IOptions<SyncOptions> syncOpts,
    IOptions<NldrOptions> nldrOpts,
    IAppLogger<NldrIngestService> logger,
    IErrorFactory errorFactory,
    IFaultInjector faultInjector,
    IClock clock) : INldrIngestService
{
    public async Task<IngestResponse> IngestAsync(
        EventEnvelope envelope,
        string testToken,
        CancellationToken ct = default)
    {
        using var op = logger.BeginOperation("Nldr", "Sync", "Ingest");
        logger.Information("Ingest event={EventId} pacs={PacsId} seq={Seq}",
            envelope.EventId, envelope.PacsId, envelope.SequenceNo);

        // ── Step 2: Validate mTLS / test token ───────────────────────────────
        if (nldrOpts.Value.Iam.Enabled && string.IsNullOrWhiteSpace(testToken))
            errorFactory.Throw("ERP-NLDR-SEC-0001", "Authentication failed — no token presented.");

        // ── Step 3: Validate required envelope fields ─────────────────────────
        if (string.IsNullOrWhiteSpace(envelope.EventId))
            errorFactory.Throw("ERP-NLDR-VAL-0003", "Missing eventId.");

        if (envelope.SequenceNo <= 0)
            errorFactory.Throw("ERP-NLDR-VAL-0004", "sequenceNo must be a positive integer.");

        // ── Step 3b: clock drift check ────────────────────────────────────────
        var driftSeconds = (clock.UtcNow - envelope.CreatedAtUtc).TotalSeconds;
        var maxDrift = syncOpts.Value.ClockDrift.MaxAllowedSeconds;
        if (driftSeconds < -(maxDrift) || driftSeconds > maxDrift * 10)
            errorFactory.Throw("ERP-NLDR-VAL-0005",
                $"Event timestamp drift {driftSeconds:F0}s exceeds allowed {maxDrift}s.");

        // ── Step 4: Check fault hook mode ─────────────────────────────────────
        var mode = harness.Value.TestMode
            ? testState.ConsumeAndMaybeReset()
            : NldrMode.Healthy;

        if (mode == NldrMode.Http500)
            throw new InvalidOperationException("TestMode: simulated 500");

        if (mode == NldrMode.Timeout)
            await Task.Delay(testState.DelayMs, ct);

        if (mode == NldrMode.RateLimit)
        {
            // Caller handles 429 via middleware — we just signal it
            throw new NldrRateLimitException(testState.RetryAfterSec);
        }

        // ── Step 5: Recompute and compare payload_hash ────────────────────────
        var strictHash = mode == NldrMode.HashStrict || !harness.Value.TestMode;
        if (strictHash && !PayloadHasher.Verify(envelope))
            errorFactory.Throw("ERP-NLDR-VAL-0002",
                $"Payload hash mismatch for event {envelope.EventId}.");

        // ── Step 7: Change-type specific constraints ──────────────────────────
        if (envelope.ChangeType == ChangeType.DELETE && envelope.BeforeState is null)
            errorFactory.Throw("ERP-NLDR-VAL-0006", "DELETE event missing beforeState.");

        if (envelope.ChangeType == ChangeType.AMENDMENT)
        {
            if (string.IsNullOrWhiteSpace(envelope.AmendmentMeta?.Reason) ||
                string.IsNullOrWhiteSpace(envelope.AmendmentMeta?.Approver))
                errorFactory.Throw("ERP-NLDR-VAL-0007", "AMENDMENT missing reason or approver.");
        }

        // ── Open TX (steps 6–12 are inside one MySQL transaction) ─────────────
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);

        try
        {
            // ── Step 6: Validate sequence ─────────────────────────────────────
            var lastAcked = await repo.GetLastAckedSequenceAsync(db, tx, envelope.PacsId, ct);
            var seq       = envelope.SequenceNo;

            string applyStatus;
            string? rejectReason = null;

            if (seq <= lastAcked)
            {
                // Potential duplicate or replay
                var exists = await repo.EventExistsAsync(db, tx, envelope.EventId, ct);
                if (exists)
                {
                    applyStatus = ApplyStatus.Duplicate;
                }
                else
                {
                    // Same sequence, different event_id → replayed with altered payload
                    errorFactory.Throw("ERP-NLDR-SEC-0002",
                        $"Replayed event with altered payload (seq={seq}, lastAcked={lastAcked}).");
                    applyStatus = ApplyStatus.Rejected; // never reached
                }
            }
            else if (seq == lastAcked + 1 || mode == NldrMode.SequenceStrict && seq == lastAcked + 1)
            {
                applyStatus = ApplyStatus.Applied;
            }
            else if (mode == NldrMode.SequenceStrict)
            {
                applyStatus = ApplyStatus.Rejected;
                rejectReason = $"Gap detected: expected {lastAcked + 1}, got {seq}";
            }
            else
            {
                // GAP_WAITING — record gap rows
                await repo.InsertSequenceGapsAsync(db, tx, envelope.PacsId, lastAcked, seq, ct);
                applyStatus = ApplyStatus.GapWaiting;
            }

            await faultInjector.FireAsync(FaultHook.BeforeInboxApply, ct);

            // ── Step 8: Apply business state ──────────────────────────────────
            if (applyStatus == ApplyStatus.Applied)
            {
                JsonElement? payloadEl   = TryParseElement(envelope.Payload);
                JsonElement? beforeEl    = TryParseElement(envelope.BeforeState);
                string?      deleteReason = envelope.AmendmentMeta?.Reason
                                         ?? (envelope.BeforeState != null ? "deleted" : null);

                await repo.ApplyBusinessStateAsync(
                    db, tx,
                    envelope.EntityType, envelope.ChangeType.ToString(),
                    envelope.EntityId,
                    payloadEl, beforeEl, deleteReason,
                    envelope.CorrelationId, ct);
            }

            // ── Step 9: Insert received_event ─────────────────────────────────
            await repo.InsertReceivedEventAsync(
                db, tx,
                envelope.EventId, envelope.PacsId, seq,
                envelope.ChangeType.ToString(), envelope.EntityType, envelope.EntityId,
                SerializeObj(envelope.Payload), SerializeObj(envelope.BeforeState),
                envelope.PayloadHash,
                applyStatus, rejectReason,
                envelope.CorrelationId, ct);

            // ── Step 10: Inbox dedupe row ─────────────────────────────────────
            await SyncInboxStore.TryInsertAsync(
                db, tx,
                envelope.EventId, "PACS", seq,
                envelope.PayloadHash, envelope.IdempotencyKey,
                envelope.CorrelationId, ct);

            // ── Step 11: Enqueue ACK ──────────────────────────────────────────
            var ackStatus = applyStatus is ApplyStatus.Applied or ApplyStatus.Duplicate
                ? "ACK"
                : "NACK";

            if (mode != NldrMode.DropAck)
            {
                await repo.EnqueueAckAsync(
                    db, tx,
                    envelope.EventId, envelope.PacsId, seq,
                    ackStatus, rejectReason,
                    envelope.CorrelationId,
                    syncOpts.Value.AcksTopic, ct);
            }

            if (applyStatus == ApplyStatus.Applied)
                await repo.UpdateNldrCheckpointAsync(db, tx, envelope.PacsId, seq, ct);

            await tx.CommitAsync(ct);

            await faultInjector.FireAsync(FaultHook.AfterInboxApply, ct);

            logger.Checkpoint("IngestComplete", new Dictionary<string, object?>
            {
                ["EventId"]     = envelope.EventId,
                ["ApplyStatus"] = applyStatus,
                ["SeqNo"]       = seq
            });

            // ── Step 12: Return 200 ───────────────────────────────────────────
            return new IngestResponse
            {
                EventId    = envelope.EventId,
                Status     = applyStatus,
                AckedAt    = applyStatus == ApplyStatus.Applied ? clock.UtcNow : null,
                RejectReason = rejectReason
            };
        }
        catch (HarnessException)
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await tx.RollbackAsync(CancellationToken.None);
            logger.Error(ex, "Ingest failed for event {EventId}", envelope.EventId);
            throw;
        }
    }

    private static JsonElement? TryParseElement(object? obj)
    {
        if (obj is null) return null;
        try
        {
            var json = obj is string s ? s : JsonSerializer.Serialize(obj);
            return JsonDocument.Parse(json).RootElement;
        }
        catch { return null; }
    }

    private static string? SerializeObj(object? obj)
    {
        if (obj is null) return null;
        return obj is string s ? s : JsonSerializer.Serialize(obj);
    }
}

/// <summary>Signals a 429 rate-limit response to the controller.</summary>
public sealed class NldrRateLimitException(int retryAfterSec) : Exception
{
    public int RetryAfterSec { get; } = retryAfterSec;
}

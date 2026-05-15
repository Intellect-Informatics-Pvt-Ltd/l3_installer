using Harness.Common.Canonicalization;
using Harness.Common.Envelope;
using Harness.Common.Errors;
using Harness.Common.Identifiers;
using Harness.Common.Observability;
using Harness.Common.Options;
using Harness.Common.Outbox;
using Harness.Common.Sequencing;
using Harness.Common.TestHooks;
using Harness.Common.Time;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Pacs.Fas.Api.Vouchers;

public interface IVoucherService
{
    Task<VoucherDto> CreateAsync(CreateVoucherRequest request, string correlationId, CancellationToken ct = default);
}

public sealed class VoucherService(
    MySqlConnection db,
    IVoucherRepository repo,
    IOptions<PacsOptions> pacsOptions,
    IOptions<SyncOptions> syncOptions,
    IAppLogger<VoucherService> logger,
    IErrorFactory errorFactory,
    IFaultInjector faultInjector,
    IClock clock) : IVoucherService
{
    public async Task<VoucherDto> CreateAsync(
        CreateVoucherRequest request,
        string correlationId,
        CancellationToken ct = default)
    {
        var pacsId   = pacsOptions.Value.PacsId;
        var priority = syncOptions.Value.Priority.VoucherDefault;

        using var op = logger.BeginOperation("Pacs", "Fas", "CreateVoucher");
        logger.Information("Creating voucher {VoucherNo} for PACS {PacsId}", request.VoucherNo, pacsId);

        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);

        try
        {
            // ── Step 4: INSERT voucher + lines ─────────────────────────────────
            var totalAmount = request.Lines.Sum(l => l.DebitAmount + l.CreditAmount) / 2m;

            await faultInjector.FireAsync(FaultHook.BeforeDbCommit, ct);

            var voucherId = await repo.InsertVoucherAsync(
                db, tx,
                pacsId, request.VoucherNo, request.VoucherDate, request.VoucherType,
                request.Narration, totalAmount, request.CreatedBy, correlationId, ct);

            foreach (var line in request.Lines)
            {
                await repo.InsertVoucherLineAsync(
                    db, tx, voucherId,
                    line.AccountCode, line.DebitAmount, line.CreditAmount,
                    line.LineNarration, ct);
            }

            // ── Step 5-6: Compute hash + allocate sequence_no ─────────────────
            var afterState = new
            {
                voucherId,
                pacsId,
                request.VoucherNo,
                request.VoucherDate,
                request.VoucherType,
                request.Narration,
                totalAmount,
                status = "POSTED",
                request.CreatedBy
            };

            var seqNo = await SequenceAllocator.GetNextAsync(
                db, tx, pacsId, "pacs.outbound", ct);

            var voucherIdStr = voucherId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var idempKey = IdempotencyKey.Format(
                pacsId, "voucher", voucherIdStr, "INSERT", clock.UtcNow);

            var envelope = new EventEnvelopeBuilder()
                .WithCorrelation(correlationId)
                .WithPacsId(pacsId)
                .WithSequenceNo(seqNo)
                .WithIdempotencyKey(idempKey)
                .WithChangeType(ChangeType.INSERT)
                .WithEntityType("voucher")
                .WithEntityId(voucherIdStr)
                .WithPayload(afterState)
                .Build(clock);

            // ── Step 7: INSERT sync_outbox in same tx ─────────────────────────
            await SyncOutboxWriter.WriteAsync(db, tx, envelope, priority, ct);

            logger.Checkpoint("OutboxEnqueued", new Dictionary<string, object?>
            {
                ["VoucherId"] = voucherId,
                ["SequenceNo"] = seqNo,
                ["EventId"] = envelope.EventId
            });

            await tx.CommitAsync(ct);

            await faultInjector.FireAsync(FaultHook.AfterDbCommit, ct);

            logger.Information("Voucher {VoucherId} created, seq={SeqNo}", voucherId, seqNo);

            return new VoucherDto
            {
                VoucherId        = voucherId,
                PacsId           = pacsId,
                VoucherNo        = request.VoucherNo,
                VoucherDate      = request.VoucherDate,
                VoucherType      = request.VoucherType,
                Narration        = request.Narration,
                TotalAmount      = totalAmount,
                Status           = "POSTED",
                CorrelationId    = correlationId,
                OutboxSequenceNo = seqNo
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await tx.RollbackAsync(CancellationToken.None);
            logger.Error(ex, "Failed to create voucher {VoucherNo}", request.VoucherNo);
            throw errorFactory.FromCatalog("ERP-PACS-INS-0001", ex.Message);
        }
    }
}

using Dapper;
using MySqlConnector;
using System.Data;

namespace Pacs.Fas.Api.Vouchers;

public interface IVoucherRepository
{
    Task<long> InsertVoucherAsync(
        IDbConnection conn, IDbTransaction tx,
        string pacsId, string voucherNo, DateOnly voucherDate,
        string voucherType, string? narration, decimal totalAmount,
        string createdBy, string correlationId,
        CancellationToken ct = default);

    Task InsertVoucherLineAsync(
        IDbConnection conn, IDbTransaction tx,
        long voucherId, string accountCode,
        decimal debitAmount, decimal creditAmount,
        string? lineNarration,
        CancellationToken ct = default);

    Task<VoucherRow?> GetByIdAsync(
        IDbConnection conn, long voucherId,
        CancellationToken ct = default);
}

public sealed class VoucherRepository : IVoucherRepository
{
    public async Task<long> InsertVoucherAsync(
        IDbConnection conn, IDbTransaction tx,
        string pacsId, string voucherNo, DateOnly voucherDate,
        string voucherType, string? narration, decimal totalAmount,
        string createdBy, string correlationId,
        CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO voucher
                (pacs_id, voucher_no, voucher_date, voucher_type, narration,
                 total_amount, status, is_deleted, created_by, created_at, correlation_id)
            VALUES
                (@pacsId, @voucherNo, @voucherDate, @voucherType, @narration,
                 @totalAmount, 'POSTED', 0, @createdBy, NOW(6), @correlationId)
            """;

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            pacsId, voucherNo, voucherDate, voucherType, narration,
            totalAmount, createdBy, correlationId
        }, tx, cancellationToken: ct));

        return await conn.QuerySingleAsync<long>(
            new CommandDefinition("SELECT LAST_INSERT_ID()", null, tx, cancellationToken: ct));
    }

    public async Task InsertVoucherLineAsync(
        IDbConnection conn, IDbTransaction tx,
        long voucherId, string accountCode,
        decimal debitAmount, decimal creditAmount,
        string? lineNarration,
        CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO voucher_line
                (voucher_id, account_code, debit_amount, credit_amount, line_narration)
            VALUES
                (@voucherId, @accountCode, @debitAmount, @creditAmount, @lineNarration)
            """;

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            voucherId, accountCode, debitAmount, creditAmount, lineNarration
        }, tx, cancellationToken: ct));
    }

    public async Task<VoucherRow?> GetByIdAsync(
        IDbConnection conn, long voucherId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT voucher_id        AS VoucherId,
                   pacs_id           AS PacsId,
                   voucher_no        AS VoucherNo,
                   voucher_date      AS VoucherDate,
                   voucher_type      AS VoucherType,
                   narration         AS Narration,
                   total_amount      AS TotalAmount,
                   status            AS Status,
                   correlation_id    AS CorrelationId,
                   created_at        AS CreatedAt
              FROM voucher
             WHERE voucher_id = @voucherId
            """;

        return await conn.QueryFirstOrDefaultAsync<VoucherRow>(
            new CommandDefinition(sql, new { voucherId }, cancellationToken: ct));
    }
}

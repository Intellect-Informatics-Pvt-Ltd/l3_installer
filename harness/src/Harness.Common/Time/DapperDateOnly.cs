using System.Data;
using System.Globalization;
using Dapper;

namespace Harness.Common.Time;

/// <summary>
/// Lets Dapper bind and read <see cref="DateOnly"/> - registered once by
/// <see cref="Extensions.HarnessCommonExtensions.AddHarnessCommon"/>, so every harness API gets it.
/// </summary>
/// <remarks>
/// <para><b>Why it exists.</b> The voucher contract carries <c>VoucherDate</c> as a <see cref="DateOnly"/>
/// (<c>Pacs.Fas.Api/Vouchers/VoucherModels.cs</c>, <c>Nldr.Api/Sync/NldrIngestRepository.cs</c>), and Dapper
/// 2.1.35 does not know the type: every voucher create failed with "The member voucherDate of type
/// System.DateOnly cannot be used as a parameter value" and the API answered 500. Nothing had noticed,
/// because the one test that creates a voucher (<c>Harness.IntegrationTests</c>, HappyPath) never started -
/// its run-settings file was missing - and the Debian e2e that installs this API has not been run.
/// Found 2026-10-02 when that test first ran.</para>
/// <para><b>Shape.</b> Written as <see cref="DbType.Date"/> from midnight of the day, so the column receives
/// a date and nothing a time zone could shift. Read back from whatever the driver hands over -
/// MySqlConnector returns <see cref="DateTime"/> for DATE columns, or <see cref="DateOnly"/> when the
/// connection asks for it - so either connection string works. Dapper applies a handler for
/// <c>T</c> to <c>T?</c> as well.</para>
/// </remarks>
public sealed class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
{
    public override void SetValue(IDbDataParameter parameter, DateOnly value)
    {
        parameter.DbType = DbType.Date;
        parameter.Value = value.ToDateTime(TimeOnly.MinValue);
    }

    public override DateOnly Parse(object value) => value switch
    {
        DateOnly d => d,
        DateTime dt => DateOnly.FromDateTime(dt),
        string s => DateOnly.Parse(s, CultureInfo.InvariantCulture),
        _ => DateOnly.FromDateTime(Convert.ToDateTime(value, CultureInfo.InvariantCulture)),
    };
}

/// <summary>Registers the harness's Dapper type handlers exactly once per process.</summary>
public static class DapperTypeHandlers
{
    private static int _registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
    }
}

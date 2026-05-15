using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Pacs.Fas.Api.Vouchers;

// ── Request / Response DTOs ────────────────────────────────────────────────

public sealed class CreateVoucherRequest
{
    [Required, MinLength(1)]
    public string VoucherNo { get; init; } = string.Empty;

    [Required]
    public DateOnly VoucherDate { get; init; }

    [Required, MinLength(1)]
    public string VoucherType { get; init; } = string.Empty;    // CR, DB, JV, PV, RV

    public string? Narration { get; init; }

    [Required, MinLength(1)]
    public string CreatedBy { get; init; } = string.Empty;

    [Required, MinLength(1)]
    public IReadOnlyList<CreateVoucherLineRequest> Lines { get; init; } = [];
}

public sealed class CreateVoucherLineRequest
{
    [Required, MinLength(1)]
    public string AccountCode { get; init; } = string.Empty;

    [Range(0, double.MaxValue)]
    public decimal DebitAmount  { get; init; }

    [Range(0, double.MaxValue)]
    public decimal CreditAmount { get; init; }

    public string? LineNarration { get; init; }
}

public sealed class VoucherDto
{
    [JsonPropertyName("voucherId")]
    public long VoucherId { get; init; }

    [JsonPropertyName("pacsId")]
    public string PacsId { get; init; } = string.Empty;

    [JsonPropertyName("voucherNo")]
    public string VoucherNo { get; init; } = string.Empty;

    [JsonPropertyName("voucherDate")]
    public DateOnly VoucherDate { get; init; }

    [JsonPropertyName("voucherType")]
    public string VoucherType { get; init; } = string.Empty;

    [JsonPropertyName("narration")]
    public string? Narration { get; init; }

    [JsonPropertyName("totalAmount")]
    public decimal TotalAmount { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; init; } = string.Empty;

    [JsonPropertyName("outboxSequenceNo")]
    public long OutboxSequenceNo { get; init; }
}

// ── Internal DB row types (used by Dapper) ─────────────────────────────────

public sealed class VoucherRow
{
    public long    VoucherId     { get; init; }
    public string  PacsId        { get; init; } = string.Empty;
    public string  VoucherNo     { get; init; } = string.Empty;
    public DateOnly VoucherDate  { get; init; }
    public string  VoucherType   { get; init; } = string.Empty;
    public string? Narration     { get; init; }
    public decimal TotalAmount   { get; init; }
    public string  Status        { get; init; } = string.Empty;
    public string  CorrelationId { get; init; } = string.Empty;
    public DateTime CreatedAt    { get; init; }
}

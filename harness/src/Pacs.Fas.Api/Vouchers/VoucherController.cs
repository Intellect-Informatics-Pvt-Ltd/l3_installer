using Harness.Common.Errors;
using Harness.Common.Identifiers;
using Microsoft.AspNetCore.Mvc;

namespace Pacs.Fas.Api.Vouchers;

[ApiController]
[Route("api/vouchers")]
public sealed class VoucherController(
    IVoucherService voucherService) : ControllerBase
{
    /// <summary>
    /// Creates a voucher and atomically writes the sync outbox row
    /// in the same DB transaction (invariant I-2).
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(VoucherDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CreateVoucher(
        [FromBody] CreateVoucherRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        // Propagate or generate correlation ID
        var correlationId = HttpContext.Request.Headers["X-Correlation-Id"].FirstOrDefault()
                            ?? EventIdProvider.NewEventId();

        try
        {
            var dto = await voucherService.CreateAsync(request, correlationId, ct);
            Response.Headers["X-Correlation-Id"] = correlationId;
            return CreatedAtAction(nameof(GetVoucher), new { id = dto.VoucherId }, dto);
        }
        catch (HarnessException hex)
        {
            return Problem(
                detail: hex.Message,
                title:  hex.ErrorCode,
                statusCode: hex.ErrorCode.StartsWith("ERP-PACS-VAL", StringComparison.Ordinal) ? 422 : 500);
        }
    }

    /// <summary>Gets a single voucher by ID.</summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType(typeof(VoucherDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetVoucher(long id)
    {
        // Stub — full implementation in M5
        return NotFound(new { message = $"Voucher {id} not found (GET not yet implemented)" });
    }
}

using Harness.Common.Envelope;
using Harness.Common.Errors;
using Microsoft.AspNetCore.Mvc;

namespace Nldr.Api.Sync;

[ApiController]
[Route("api/sync")]
public sealed class SyncIngestController(INldrIngestService ingestService) : ControllerBase
{
    /// <summary>
    /// Ingest a single event envelope from a PACS node.
    /// Executes the 12-step validation pipeline (§12.5.2).
    /// </summary>
    [HttpPost("ingest")]
    [ProducesResponseType(typeof(IngestResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Ingest(
        [FromBody] EventEnvelope envelope,
        CancellationToken ct)
    {
        // Step 1: JSON parse already done by model binding; envelope is null if malformed.
        if (envelope is null)
            return BadRequest(new { errorCode = "ERP-NLDR-VAL-0001", message = "Invalid envelope JSON." });

        // X-Test-Token header (used in TestMode instead of mTLS)
        var testToken = Request.Headers["X-Test-Token"].FirstOrDefault() ?? string.Empty;

        try
        {
            var result = await ingestService.IngestAsync(envelope, testToken, ct);
            Response.Headers["X-Correlation-Id"] = envelope.CorrelationId;
            return Ok(result);
        }
        catch (NldrRateLimitException rle)
        {
            Response.Headers["Retry-After"] = rle.RetryAfterSec.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return StatusCode(429, new { errorCode = "ERP-NLDR-RATE-0001", message = "Rate limited." });
        }
        catch (HarnessException hex)
        {
            var status = hex.ErrorCode switch
            {
                "ERP-NLDR-SEC-0001" => 401,
                "ERP-NLDR-VAL-0002" => 422,
                "ERP-NLDR-VAL-0006" => 422,
                "ERP-NLDR-VAL-0007" => 422,
                "ERP-NLDR-SEC-0002" => 422,
                "ERP-NLDR-VAL-0003" => 400,
                "ERP-NLDR-VAL-0004" => 400,
                "ERP-NLDR-VAL-0005" => 400,
                _ => 500
            };
            return StatusCode(status, new { errorCode = hex.ErrorCode, message = hex.Message });
        }
    }
}

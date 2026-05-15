using Harness.Common.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Nldr.Api.TestControl;

/// <summary>
/// NLDR TestControl routes (§13.2). Active only when Harness:TestMode=true.
/// All routes return 404 in production (guard is applied in Program.cs).
/// </summary>
[ApiController]
[Route("api/test")]
public sealed class NldrTestController(
    NldrTestState state,
    IOptions<HarnessOptions> harness) : ControllerBase
{
    // ── Failure mode ──────────────────────────────────────────────────────────

    [HttpPost("failure-mode")]
    public IActionResult SetFailureMode([FromBody] SetFailureModeRequest req)
    {
        if (!harness.Value.TestMode) return NotFound();

        var mode = Enum.TryParse<NldrMode>(req.Mode, ignoreCase: true, out var m)
            ? m
            : NldrMode.Healthy;

        state.Mode          = mode;
        state.Count         = req.Count ?? 0;
        state.RetryAfterSec = req.RetryAfterSec ?? 20;
        state.DelayMs       = req.DelayMs ?? 5000;

        return Ok(new { mode = state.Mode.ToString(), count = state.Count });
    }

    [HttpGet("state")]
    public IActionResult GetState()
    {
        if (!harness.Value.TestMode) return NotFound();
        return Ok(new
        {
            mode         = state.Mode.ToString(),
            count        = state.Count,
            retryAfterSec = state.RetryAfterSec,
            delayMs      = state.DelayMs
        });
    }

    public sealed class SetFailureModeRequest
    {
        public string  Mode          { get; init; } = "healthy";
        public int?    Count         { get; init; }
        public int?    RetryAfterSec { get; init; }
        public int?    DelayMs       { get; init; }
    }
}

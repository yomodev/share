using BatchMonitor.Models;
using BatchMonitor.Services;
using Microsoft.AspNetCore.Mvc;

namespace BatchMonitor.Controllers;

[ApiController]
[Route("api/{env}/batches")]
public class BatchesController : ControllerBase
{
    private readonly IBatchService _svc;

    public BatchesController(IBatchService svc) => _svc = svc;

    [HttpGet("{runId}/details")]
    public async Task<ActionResult<BatchDetails>> GetDetails(string env, string runId, CancellationToken ct)
    {
        try
        {
            var d = await _svc.GetBatchDetailsAsync(env, runId, ct);
            return Ok(d);
        }
        catch (OperationCanceledException) { return BadRequest(); }
        catch (Exception) { return StatusCode(500); }
    }
}
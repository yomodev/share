using NxtUI.Core.Services;
using NxtUI.Core.Models;
using NxtUI.Services;
using Microsoft.AspNetCore.Mvc;

namespace NxtUI.Controllers;

[ApiController]
[Route("api/{env}/runs")]
public class RunsController(IRunService svc) : ControllerBase
{
    [HttpGet("{runId}/details")]
    public async Task<ActionResult<RunDetails>> GetDetails(string env, string runId, CancellationToken ct)
    {
        try
        {
            var d = await svc.GetRunDetailsAsync(env, runId, ct);
            return Ok(d);
        }
        catch (OperationCanceledException) { return BadRequest(); }
        catch (Exception) { return StatusCode(500); }
    }
}

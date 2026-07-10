using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using NxtUI.Core.Services;

namespace NxtUI.Web.Services;

/// <summary>
/// Single place every page calls to cancel a run, instead of each page hitting
/// <see cref="IRunService.CancelRunAsync"/> directly. Goes through /api/runs/cancel-simulate
/// first (a stand-in for a real external cancellation call — the intended integration point
/// once there's a real orchestrator to call) and only updates local run state via
/// <see cref="IRunService"/> if that call reports success.
/// </summary>
public sealed class RunActionsService(IHttpClientFactory httpFactory, NavigationManager nav, IRunService runService)
{
    public async Task<(bool Success, string Message)> CancelRunAsync(string env, string runId, CancellationToken ct = default)
    {
        try
        {
            var client = httpFactory.CreateClient();
            var url = new Uri(new Uri(nav.BaseUri), "api/runs/cancel-simulate");
            var response = await client.PostAsJsonAsync(url, new { Env = env, RunId = runId }, ct);
            var payload = await response.Content.ReadFromJsonAsync<CancelResponse>(cancellationToken: ct);
            var message = payload?.Message ?? $"Cancel request completed with status {(int)response.StatusCode}.";

            if (!response.IsSuccessStatusCode) return (false, message);

            // The simulated backend accepted the cancellation — reflect it locally.
            var updated = await runService.CancelRunAsync(env, runId, ct);
            return (updated, updated ? message : $"{message} (run could not be updated locally — already finished?)");
        }
        catch (Exception ex)
        {
            return (false, $"Cancel request failed: {ex.Message}");
        }
    }

    private sealed record CancelResponse(string Message);
}

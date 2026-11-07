using System.Security.Claims;
using CodeRank.Api.Contracts.Judge;
using CodeRank.Api.Judge;
using CodeRank.Judge.Protocol;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace CodeRank.Api.Controllers;

[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Authorize(AuthenticationSchemes = WorkerCredentials.Scheme)]
[RequestSizeLimit(1024)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route(RoutePrefix + "/jobs")]
public sealed class JudgeJobsController(JudgeQueue queue, JudgeCompletionPolicy completion) : ControllerBase
{
    public const string RoutePrefix = "/api/v1/judge";
    private string WorkerId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [HttpPost("claim")]
    public async Task<ActionResult<JudgeJobLease>> Claim(CancellationToken token)
    {
        if (HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Claim requests must have an empty body.");

        var job = await queue.ClaimAsync(WorkerId, token);
        return job is null ? NoContent() : Ok(job);
    }

    [HttpPost("{id:long:min(1)}/heartbeat")]
    public async Task<ActionResult<JudgeHeartbeat>> Heartbeat(long id, JudgeLeaseRequest request, CancellationToken token)
    {
        var heartbeat = await queue.HeartbeatAsync(id, WorkerId, request.LeaseToken!.Value, token);
        return heartbeat is null ? LeaseLost() : Ok(heartbeat);
    }

    [HttpPost("{id:long:min(1)}/complete")]
    public async Task<IActionResult> Complete(long id, CompleteJudgeJobRequest request, CancellationToken token)
    {
        // Only the isolated test host enables this policy in stage 1.
        if (!completion.Enabled)
            return JudgeProblem(StatusCodes.Status403Forbidden, "judge_execution_disabled", "Judge execution is disabled.");

        var outcome = await queue.CompleteAsync(id, WorkerId, request.LeaseToken!.Value, request.Verdict, token);
        return outcome switch
        {
            JudgeCompletionOutcome.Saved => NoContent(),
            JudgeCompletionOutcome.VerdictConflict => JudgeProblem(StatusCodes.Status409Conflict, "verdict_conflict",
                "This lease has already completed with a different verdict."),
            _ => LeaseLost()
        };
    }

    private ObjectResult LeaseLost() => JudgeProblem(StatusCodes.Status409Conflict, "lease_lost", "The lease is no longer active.");

    private ObjectResult JudgeProblem(int status, string code, string title) =>
        Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}

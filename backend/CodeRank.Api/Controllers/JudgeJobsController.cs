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

    [HttpGet("{id:long:min(1)}/payload")]
    public async Task<IActionResult> Payload(long id, CancellationToken token)
    {
        var header = Request.Headers[JudgePayloadContract.LeaseHeader];
        if (Request.QueryString.HasValue || HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true ||
            header.Count != 1 || header[0] is not { Length: 36 } value ||
            !Guid.TryParseExact(value, "D", out var leaseToken) || leaseToken == Guid.Empty)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Send the lease token in X-CodeRank-Lease only.");

        var (outcome, payload) = await queue.ReadPayloadAsync(id, WorkerId, leaseToken, token);
        return outcome switch
        {
            JudgePayloadOutcome.Ready => new JsonResult(payload, JudgePayloadContract.JsonOptions),
            JudgePayloadOutcome.LeaseLost => LeaseLost(),
            JudgePayloadOutcome.TestsUnavailable => JudgeProblem(StatusCodes.Status409Conflict,
                JudgePayloadContract.TestsUnavailableCode, "Private tests were unavailable when the lease was granted."),
            _ => JudgeProblem(StatusCodes.Status422UnprocessableEntity,
                JudgePayloadContract.InvalidPayloadCode, "The stored job payload is invalid.")
        };
    }

    [HttpPost("{id:long:min(1)}/complete")]
    public async Task<IActionResult> Complete(long id, CompleteJudgeJobRequest request, CancellationToken token)
    {
        // Only the isolated test host enables completion before a sandbox is available.
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

    private ObjectResult LeaseLost() => JudgeProblem(StatusCodes.Status409Conflict, JudgePayloadContract.LeaseLostCode,
        "The lease is no longer active.");

    private ObjectResult JudgeProblem(int status, string code, string title) =>
        Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}

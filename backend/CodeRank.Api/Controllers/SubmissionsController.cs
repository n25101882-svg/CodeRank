using System.Data;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using CodeRank.Api.Contracts.Submissions;
using CodeRank.Api.Data;
using CodeRank.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CodeRank.Api.Controllers;

[ApiController]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/v1/submissions")]
public sealed class SubmissionsController(CodeRankDbContext db, TimeProvider clock) : ControllerBase
{
    [HttpPost("/api/v1/olympiads/{olympiadId:long:min(1)}/problems/{problemId:long:min(1)}/submissions")]
    [RequestSizeLimit(CreateSubmissionRequest.MaxRequestBytes)]
    public async Task<ActionResult<CreatedSubmission>> Create(
        long olympiadId, long problemId, CreateSubmissionRequest request, CancellationToken token)
    {
        try
        {
            // Hold eligibility reads through the insert, including concurrent detach or unpublish.
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var userId = await ActiveUserIdAsync(token);
            if (userId is null) return Forbid();

            var window = await db.OlympiadProblems.AsNoTracking()
                .Where(link => link.OlympiadId == olympiadId && link.ProblemId == problemId &&
                    link.IsVisible && link.Olympiad.IsPublished)
                .Select(link => new { link.Olympiad.StartsAtUtc, link.Olympiad.EndsAtUtc })
                .SingleOrDefaultAsync(token);
            if (window is null) return NotFound();

            var now = clock.GetUtcNow().UtcDateTime;
            if (now < window.StartsAtUtc)
                return SubmissionConflict("olympiad_not_started", "The olympiad has not started.");
            if (now >= window.EndsAtUtc)
                return SubmissionConflict("olympiad_ended", "The olympiad has ended.");

            var submission = new Submission
            {
                UserId = userId.Value,
                OlympiadId = olympiadId,
                ProblemId = problemId,
                Language = request.Language,
                SourceCode = request.SourceCode,
                SubmittedAtUtc = now
            };
            db.Submissions.Add(submission);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return CreatedAtAction(nameof(GetById), new { id = submission.Id },
                new CreatedSubmission(submission.Id, submission.Status, submission.SubmittedAtUtc));
        }
        catch (Exception error) when ((error as SqlException ?? error.InnerException as SqlException)?.Number is 1205 or 1222)
        {
            return SubmissionConflict("submission_write_conflict", "The submission could not be saved. Try again.");
        }
    }

    [HttpGet]
    public async Task<ActionResult<SubmissionHistory>> List(
        [FromQuery] SubmissionHistoryQuery query, CancellationToken token)
    {
        var userId = await ActiveUserIdAsync(token);
        if (userId is null) return Forbid();

        var owned = OwnedSubmissions(userId.Value);
        if (query.OlympiadId is long olympiadId) owned = owned.Where(item => item.OlympiadId == olympiadId);
        if (query.ProblemId is long problemId) owned = owned.Where(item => item.ProblemId == problemId);

        var ordered = query.Sort == "oldest"
            ? owned.OrderBy(item => item.SubmittedAtUtc).ThenBy(item => item.Id)
            : owned.OrderByDescending(item => item.SubmittedAtUtc).ThenByDescending(item => item.Id);
        var items = await ordered.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize + 1)
            .Select(item => new SubmissionSummary(item.Id, item.OlympiadId, item.ProblemId, item.Language,
                item.Status, DateTime.SpecifyKind(item.SubmittedAtUtc, DateTimeKind.Utc)))
            .ToListAsync(token);

        var hasExtra = items.Count > query.PageSize;
        if (hasExtra) items.RemoveAt(items.Count - 1);
        return Ok(new SubmissionHistory(items, query.Page, query.PageSize,
            hasExtra && query.Page < SubmissionHistoryQuery.MaxPage));
    }

    [HttpGet("{id:long:min(1)}")]
    public async Task<ActionResult<SubmissionDetail>> GetById(long id, CancellationToken token)
    {
        var userId = await ActiveUserIdAsync(token);
        if (userId is null) return Forbid();

        var item = await OwnedSubmissions(userId.Value).Where(item => item.Id == id)
            .Select(item => new SubmissionDetail(item.Id, item.OlympiadId, item.ProblemId, item.Language,
                item.SourceCode, item.Status, DateTime.SpecifyKind(item.SubmittedAtUtc, DateTimeKind.Utc)))
            .SingleOrDefaultAsync(token);
        return item is null ? NotFound() : Ok(item);
    }

    private IQueryable<Submission> OwnedSubmissions(long userId) =>
        db.Submissions.AsNoTracking().Where(item => item.UserId == userId);

    private async Task<long?> ActiveUserIdAsync(CancellationToken token)
    {
        if (!long.TryParse(User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value,
            NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0) return null;
        return await db.Users.AsNoTracking().AnyAsync(user => user.Id == id && user.IsActive, token) ? id : null;
    }

    private ConflictObjectResult SubmissionConflict(string code, string title) =>
        Conflict(new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = title, Extensions = { ["code"] = code } });
}

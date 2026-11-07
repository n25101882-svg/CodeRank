using System.Data;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using CodeRank.Api.Authentication;
using CodeRank.Api.Contracts.Olympiads;
using CodeRank.Api.Data;
using CodeRank.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CodeRank.Api.Controllers;

[ApiController]
[Authorize(Policy = ActiveAdminRequirement.Policy)]
[Route("api/v1/admin/olympiads")]
public sealed class AdminOlympiadsController(CodeRankDbContext db) : ControllerBase
{
    private long AdminId => ActiveAdminRequirement.Subject(User)!.Value;

    [HttpGet]
    public async Task<ActionResult<List<AdminOlympiadResponse>>> List(CancellationToken token) =>
        Ok(await db.Olympiads.AsNoTracking()
            .OrderByDescending(olympiad => olympiad.StartsAtUtc).ThenBy(olympiad => olympiad.Id)
            .Select(ToResponse(AdminId)).ToListAsync(token));

    [HttpGet("problems")]
    public async Task<ActionResult<List<AdminOlympiadProblemOption>>> ProblemOptions(CancellationToken token) =>
        Ok(await ShareableProblems().AsNoTracking()
            .OrderBy(problem => problem.Title).ThenBy(problem => problem.Id)
            .Select(problem => new AdminOlympiadProblemOption(problem.Id, problem.Title)).ToListAsync(token));

    [HttpGet("{id:long:min(1)}")]
    public async Task<ActionResult<AdminOlympiadDetailResponse>> Get(long id, CancellationToken token)
    {
        var olympiad = await db.Olympiads.AsNoTracking().Where(olympiad => olympiad.Id == id)
            .Select(ToResponse(AdminId)).SingleOrDefaultAsync(token);
        if (olympiad is null) return NotFound();

        var links = await Links(id).AsNoTracking().OrderBy(link => link.Position)
            .Select(link => new AdminOlympiadLinkResponse(link.ProblemId,
                link.Problem.CreatedByUser.Role == ActiveAdminRequirement.Role ? link.Problem.Title : null,
                link.Position, link.MaxPoints, link.IsVisible))
            .ToListAsync(token);
        return Ok(new AdminOlympiadDetailResponse(olympiad, new(Revision(links), links)));
    }

    [HttpPost]
    public async Task<ActionResult<AdminOlympiadResponse>> Create(AdminOlympiadRequest request, CancellationToken token)
    {
        var olympiad = new Olympiad { CreatedByUserId = AdminId, IsPublished = false };
        ApplyDetails(olympiad, request);
        db.Olympiads.Add(olympiad);
        await db.SaveChangesAsync(token);
        var response = new AdminOlympiadResponse(olympiad.Id, olympiad.Title, olympiad.Description,
            olympiad.StartsAtUtc, olympiad.EndsAtUtc, olympiad.IsPublished, IsOwn: true);
        return CreatedAtAction(nameof(Get), new { id = olympiad.Id }, response);
    }

    [HttpPut("{id:long:min(1)}")]
    public async Task<IActionResult> Update(long id, AdminOlympiadRequest request, CancellationToken token)
    {
        var olympiad = await db.Olympiads.FindAsync([id], token);
        if (olympiad is null) return NotFound();
        ApplyDetails(olympiad, request);
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { return NotFound(); }
        return NoContent();
    }

    [HttpPut("{id:long:min(1)}/publication")]
    public async Task<IActionResult> SetPublication(long id, AdminOlympiadPublicationRequest request, CancellationToken token)
    {
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var olympiad = await db.Olympiads.FindAsync([id], token);
            if (olympiad is null) return NotFound();

            var published = request.IsPublished!.Value;
            if (published && await Links(id).AnyAsync(link =>
                link.IsVisible && link.Problem.CreatedByUser.Role != ActiveAdminRequirement.Role, token))
                return LinkConflict("unavailable_visible_problem", "Hide or detach unavailable problems before publishing.");

            olympiad.IsPublished = published;
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return NoContent();
        }
        catch (Exception error) when (IsWriteConflict(error))
        {
            return LinkConflict("concurrent_change", "Olympiad changed. Reload and try again.");
        }
    }

    [HttpPut("{id:long:min(1)}/problems")]
    public async Task<ActionResult<AdminOlympiadLinksResponse>> ReplaceProblems(
        long id, AdminOlympiadLinksRequest request, CancellationToken token)
    {
        var requested = request.Items.Select(item => item!).ToList();
        if (requested.Select(item => item.ProblemId).Distinct().Count() != requested.Count)
            return LinkConflict("duplicate_problem", "A problem can be linked only once.");
        if (requested.Select(item => item.Position).Distinct().Count() != requested.Count)
            return LinkConflict("duplicate_position", "Problem positions must be unique.");

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            if (!await db.Olympiads.AnyAsync(olympiad => olympiad.Id == id, token)) return NotFound();
            var current = await Links(id).ToListAsync(token);
            var existingIds = current.Select(link => link.ProblemId).ToHashSet();
            var titles = await ShareableTitlesAsync(requested, token);
            var items = new List<AdminOlympiadLinkResponse>(requested.Count);
            foreach (var item in requested.OrderBy(item => item.Position))
            {
                // A legacy link can be retained as hidden or detached. New links must use the admin catalog.
                if (!titles.TryGetValue(item.ProblemId, out var title) &&
                    (!existingIds.Contains(item.ProblemId) || item.IsVisible!.Value))
                    return NotFound();
                items.Add(new(item.ProblemId, title, item.Position, item.MaxPoints, item.IsVisible!.Value));
            }

            var currentRevision = Revision(current.Select(link =>
                new AdminOlympiadLinkResponse(link.ProblemId, null, link.Position, link.MaxPoints, link.IsVisible)));
            var revision = Revision(items);
            if (revision != currentRevision)
            {
                if (request.Revision != currentRevision)
                    return LinkConflict("stale_links", "Olympiad problems changed. Reload the list before saving.");
                await ApplyLinksAsync(id, current, items, token);
            }
            await transaction.CommitAsync(token);
            return Ok(new AdminOlympiadLinksResponse(revision, items));
        }
        catch (Exception error) when (IsWriteConflict(error))
        {
            return LinkConflict("concurrent_change", "Olympiad changed. Reload and try again.");
        }
    }

    private IQueryable<Problem> ShareableProblems() =>
        db.Problems.Where(problem => problem.CreatedByUser.Role == ActiveAdminRequirement.Role);

    private async Task<Dictionary<long, string>> ShareableTitlesAsync(
        List<AdminOlympiadLinkRequest> requested, CancellationToken token)
    {
        var titles = new Dictionary<long, string>();
        // Bound the number of parameters in each SQL Server query.
        foreach (var ids in requested.Select(item => item.ProblemId).Chunk(500))
        {
            var matches = await ShareableProblems().Where(problem => ids.Contains(problem.Id))
                .Select(problem => new { problem.Id, problem.Title }).ToListAsync(token);
            foreach (var match in matches) titles.Add(match.Id, match.Title);
        }
        return titles;
    }

    private IQueryable<OlympiadProblem> Links(long id) => db.OlympiadProblems.Where(link => link.OlympiadId == id);

    private static Expression<Func<Olympiad, AdminOlympiadResponse>> ToResponse(long adminId) => olympiad =>
        new(olympiad.Id, olympiad.Title, olympiad.Description, DateTime.SpecifyKind(olympiad.StartsAtUtc, DateTimeKind.Utc),
            DateTime.SpecifyKind(olympiad.EndsAtUtc, DateTimeKind.Utc), olympiad.IsPublished, olympiad.CreatedByUserId == adminId);

    private static void ApplyDetails(Olympiad olympiad, AdminOlympiadRequest request)
    {
        olympiad.Title = request.Title;
        olympiad.Description = request.Description;
        olympiad.StartsAtUtc = request.StartsAtUtc!.Value;
        olympiad.EndsAtUtc = request.EndsAtUtc!.Value;
    }

    private async Task ApplyLinksAsync(long id, List<OlympiadProblem> current,
        List<AdminOlympiadLinkResponse> items, CancellationToken token)
    {
        var pending = items.ToDictionary(item => item.ProblemId);
        foreach (var link in current)
        {
            if (pending.TryGetValue(link.ProblemId, out var item) &&
                link.Position == item.Position && link.MaxPoints == item.MaxPoints && link.IsVisible == item.IsVisible)
                pending.Remove(link.ProblemId);
            else
                db.OlympiadProblems.Remove(link);
        }

        // Free positions first so swaps respect the unique index. Both writes share the caller's transaction.
        await db.SaveChangesAsync(token);
        db.OlympiadProblems.AddRange(pending.Values.Select(item => new OlympiadProblem
        {
            OlympiadId = id, ProblemId = item.ProblemId, Position = item.Position,
            MaxPoints = item.MaxPoints, IsVisible = item.IsVisible
        }));
        await db.SaveChangesAsync(token);
    }

    private static string Revision(IEnumerable<AdminOlympiadLinkResponse> items)
    {
        var data = string.Join('\n', items.OrderBy(item => item.ProblemId).Select(item =>
            FormattableString.Invariant($"{item.ProblemId}:{item.Position}:{item.MaxPoints}:{item.IsVisible}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
    }

    private static bool IsWriteConflict(Exception error) =>
        error is DbUpdateConcurrencyException ||
        (error as SqlException ?? error.InnerException as SqlException)?.Number is 1205 or 1222 or 2601 or 2627 or 547;

    private ConflictObjectResult LinkConflict(string code, string title) =>
        Conflict(new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = title, Extensions = { ["code"] = code } });
}

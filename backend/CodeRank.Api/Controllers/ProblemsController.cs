using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using CodeRank.Api.Contracts.Problems;
using CodeRank.Api.Data;
using CodeRank.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CodeRank.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/problems")]
public sealed class ProblemsController(CodeRankDbContext db, IProblemDeletionConflictDetector conflicts) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<ProblemResponse>>> List(CancellationToken token)
    {
        var userId = await ActiveUserIdAsync(token);
        if (userId is null) return Unauthorized();

        var problems = await OwnedProblems(userId.Value).AsNoTracking()
            .OrderBy(problem => problem.Id).ToListAsync(token);
        return Ok(problems.Select(ToResponse).ToList());
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<ProblemResponse>> GetById(long id, CancellationToken token)
    {
        var userId = await ActiveUserIdAsync(token);
        if (userId is null) return Unauthorized();

        var problem = await OwnedProblems(userId.Value).AsNoTracking()
            .SingleOrDefaultAsync(problem => problem.Id == id, token);
        return problem is null ? NotFound() : Ok(ToResponse(problem));
    }

    [HttpPost]
    public async Task<ActionResult<ProblemResponse>> Create(ProblemRequest request, CancellationToken token)
    {
        var userId = await ActiveUserIdAsync(token);
        if (userId is null) return Unauthorized();

        var problem = new Problem { CreatedByUserId = userId.Value };
        ApplyRequest(problem, request);
        db.Problems.Add(problem);
        await db.SaveChangesAsync(token);
        return CreatedAtAction(nameof(GetById), new { id = problem.Id }, ToResponse(problem));
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, ProblemRequest request, CancellationToken token)
    {
        var userId = await ActiveUserIdAsync(token);
        if (userId is null) return Unauthorized();

        var problem = await OwnedProblems(userId.Value).SingleOrDefaultAsync(problem => problem.Id == id, token);
        if (problem is null) return NotFound();
        ApplyRequest(problem, request);
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { return NotFound(); }
        return NoContent();
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken token)
    {
        var userId = await ActiveUserIdAsync(token);
        if (userId is null) return Unauthorized();

        var problem = await OwnedProblems(userId.Value).SingleOrDefaultAsync(problem => problem.Id == id, token);
        if (problem is null) return NotFound();
        if (await HasOlympiadLinksAsync(id, token)) return LinkedProblemConflict();

        db.Problems.Remove(problem);
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { return NotFound(); }
        catch (DbUpdateException error) when (conflicts.IsConstraintViolation(error))
        {
            db.ChangeTracker.Clear();
            // A link can appear after the pre-check. Do not misreport unrelated DB failures as 409.
            if (await HasOlympiadLinksAsync(id, token)) return LinkedProblemConflict();
            throw;
        }
        return NoContent();
    }

    private async Task<long?> ActiveUserIdAsync(CancellationToken token)
    {
        if (!long.TryParse(User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value,
            NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0) return null;
        return await db.Users.AsNoTracking().AnyAsync(user => user.Id == id && user.IsActive, token) ? id : null;
    }

    private IQueryable<Problem> OwnedProblems(long userId) =>
        db.Problems.Where(problem => problem.CreatedByUserId == userId);

    private Task<bool> HasOlympiadLinksAsync(long id, CancellationToken token) =>
        db.OlympiadProblems.AsNoTracking().AnyAsync(link => link.ProblemId == id, token);

    private ConflictObjectResult LinkedProblemConflict() => Conflict(new ProblemDetails
    {
        Status = StatusCodes.Status409Conflict,
        Title = "The problem is linked to an olympiad and cannot be deleted."
    });

    private static void ApplyRequest(Problem problem, ProblemRequest request)
    {
        problem.Title = request.Title;
        problem.Statement = request.Statement;
        problem.InputFormat = request.InputFormat;
        problem.OutputFormat = request.OutputFormat;
        problem.Difficulty = request.Difficulty;
    }

    private static ProblemResponse ToResponse(Problem problem) => new(
        problem.Id, problem.Title, problem.Statement, problem.InputFormat, problem.OutputFormat,
        problem.Difficulty, DateTime.SpecifyKind(problem.CreatedAtUtc, DateTimeKind.Utc));
}

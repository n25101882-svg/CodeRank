using System.Linq.Expressions;
using CodeRank.Api.Contracts.Olympiads;
using CodeRank.Api.Data;
using CodeRank.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CodeRank.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/olympiads")]
public sealed class OlympiadsController(CodeRankDbContext db) : ControllerBase
{
    private static readonly Expression<Func<Olympiad, OlympiadResponse>> ToResponse = olympiad =>
        new OlympiadResponse(
            olympiad.Id,
            olympiad.Title,
            olympiad.Description,
            // SQL datetime2 has no timezone metadata; these columns already contain UTC.
            DateTime.SpecifyKind(olympiad.StartsAtUtc, DateTimeKind.Utc),
            DateTime.SpecifyKind(olympiad.EndsAtUtc, DateTimeKind.Utc));

    [HttpGet]
    public async Task<ActionResult<List<OlympiadResponse>>> List(CancellationToken token)
    {
        var olympiads = await PublishedOlympiads()
            .OrderBy(olympiad => olympiad.StartsAtUtc)
            .ThenBy(olympiad => olympiad.Id)
            .Select(ToResponse)
            .ToListAsync(token);

        return Ok(olympiads);
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<OlympiadResponse>> GetById(long id, CancellationToken token)
    {
        var olympiad = await PublishedOlympiads()
            .Where(olympiad => olympiad.Id == id)
            .Select(ToResponse)
            .SingleOrDefaultAsync(token);

        return olympiad is null ? NotFound() : Ok(olympiad);
    }

    private IQueryable<Olympiad> PublishedOlympiads() =>
        db.Olympiads.AsNoTracking().Where(olympiad => olympiad.IsPublished);
}

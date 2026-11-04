namespace CodeRank.Api.Contracts.Olympiads;

public sealed record OlympiadResponse(
    long Id,
    string Title,
    string? Description,
    DateTime StartsAtUtc,
    DateTime EndsAtUtc);

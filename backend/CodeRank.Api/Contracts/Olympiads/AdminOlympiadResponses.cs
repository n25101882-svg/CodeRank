namespace CodeRank.Api.Contracts.Olympiads;

public sealed record AdminOlympiadResponse(
    long Id, string Title, string? Description, DateTime StartsAtUtc, DateTime EndsAtUtc,
    bool IsPublished, bool IsOwn);

public sealed record AdminOlympiadDetailResponse(AdminOlympiadResponse Olympiad, AdminOlympiadLinksResponse Problems);

public sealed record AdminOlympiadLinksResponse(string Revision, List<AdminOlympiadLinkResponse> Items);

public sealed record AdminOlympiadLinkResponse(long ProblemId, string? Title, int Position, int MaxPoints, bool IsVisible);

public sealed record AdminOlympiadProblemOption(long Id, string Title);

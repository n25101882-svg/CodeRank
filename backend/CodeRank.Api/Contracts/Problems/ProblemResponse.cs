namespace CodeRank.Api.Contracts.Problems;

public sealed record ProblemResponse(
    long Id,
    string Title,
    string Statement,
    string? InputFormat,
    string? OutputFormat,
    string? Difficulty,
    DateTime CreatedAtUtc);

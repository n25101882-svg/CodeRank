namespace CodeRank.Api.Contracts.Olympiads;

public sealed record OlympiadProblemSummary(
    long Id,
    string Title,
    string? Difficulty,
    int MaxPoints);

public sealed record OlympiadProblemDetail(
    long Id,
    string Title,
    string? Difficulty,
    int MaxPoints,
    string Statement,
    string? InputFormat,
    string? OutputFormat,
    List<OlympiadProblemExample> Examples);

public sealed record OlympiadProblemExample(
    string InputData,
    string ExpectedOutput,
    string? Explanation);

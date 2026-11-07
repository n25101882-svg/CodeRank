namespace CodeRank.Api.Contracts.Submissions;

public sealed record CreatedSubmission(long Id, string Status, DateTime SubmittedAtUtc);

public sealed record SubmissionSummary(
    long Id, long OlympiadId, long ProblemId, string Language, string Status, DateTime SubmittedAtUtc);

public sealed record SubmissionDetail(
    long Id, long OlympiadId, long ProblemId, string Language, string SourceCode, string Status, DateTime SubmittedAtUtc);

public sealed record SubmissionHistory(
    IReadOnlyList<SubmissionSummary> Items, int Page, int PageSize, bool HasMore);

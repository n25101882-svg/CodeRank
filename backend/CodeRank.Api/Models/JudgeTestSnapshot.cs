namespace CodeRank.Api.Models;

public sealed class JudgeTestSnapshot
{
    public long SubmissionId { get; init; }
    public string TestCasesJson { get; init; } = null!;
}

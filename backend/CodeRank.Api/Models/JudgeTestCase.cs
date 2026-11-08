namespace CodeRank.Api.Models;

public sealed class JudgeTestCase
{
    public long Id { get; set; }
    public long ProblemId { get; set; }
    public int Position { get; set; }
    public string Input { get; set; } = null!;
    public string ExpectedOutput { get; set; } = null!;
    public bool IsActive { get; set; }
}

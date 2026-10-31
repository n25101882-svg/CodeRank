namespace CodeRank.Api.Models;

public partial class ProblemExample
{
    public long Id { get; set; }

    public long ProblemId { get; set; }

    public int Position { get; set; }

    public string InputData { get; set; } = null!;

    public string ExpectedOutput { get; set; } = null!;

    public string? Explanation { get; set; }

    public virtual Problem Problem { get; set; } = null!;
}

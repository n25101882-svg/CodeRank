namespace CodeRank.Api.Models;

public partial class OlympiadProblem
{
    public long OlympiadId { get; set; }

    public long ProblemId { get; set; }

    public int Position { get; set; }

    public int MaxPoints { get; set; } = 100;

    public bool IsVisible { get; set; } = true;

    public virtual Olympiad Olympiad { get; set; } = null!;
    public virtual Problem Problem { get; set; } = null!;
}

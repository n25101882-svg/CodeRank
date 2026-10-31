namespace CodeRank.Api.Models;

public partial class Problem
{
    public long Id { get; set; }

    public string Title { get; set; } = null!;

    public string Statement { get; set; } = null!;

    public string? InputFormat { get; set; }

    public string? OutputFormat { get; set; }

    public string? Difficulty { get; set; }

    public long CreatedByUserId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public virtual User CreatedByUser { get; set; } = null!;
    public virtual ICollection<OlympiadProblem> OlympiadProblems { get; set; } = new List<OlympiadProblem>();
    public virtual ICollection<ProblemExample> Examples { get; set; } = new List<ProblemExample>();
}

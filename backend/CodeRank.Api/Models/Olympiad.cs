namespace CodeRank.Api.Models;

public partial class Olympiad
{
    public long Id { get; set; }

    public string Title { get; set; } = null!;

    public string? Description { get; set; }

    public DateTime StartsAtUtc { get; set; }

    public DateTime EndsAtUtc { get; set; }

    public bool IsPublished { get; set; }

    public long CreatedByUserId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public virtual User CreatedByUser { get; set; } = null!;
    public virtual ICollection<OlympiadProblem> OlympiadProblems { get; set; } = new List<OlympiadProblem>();
}

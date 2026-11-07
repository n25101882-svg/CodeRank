namespace CodeRank.Api.Models;

public sealed class Submission
{
    public const string Cpp17 = "cpp17";
    public const string Python3 = "python3";
    public const string Queued = "Queued";

    public long Id { get; set; }
    public long UserId { get; set; }
    public long OlympiadId { get; set; }
    public long ProblemId { get; set; }
    public string Language { get; set; } = null!;
    public string SourceCode { get; set; } = null!;
    public string Status { get; set; } = Queued;
    public DateTime SubmittedAtUtc { get; set; }
}

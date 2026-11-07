namespace CodeRank.Api.Models;

public sealed class Submission
{
    public const string Cpp17 = "cpp17";
    public const string Python3 = "python3";
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string SystemError = "SystemError";

    public static bool IsFinalVerdict(string? status) =>
        status is "Accepted" or "WrongAnswer" or "CompilationError" or "RuntimeError" or
            "TimeLimitExceeded" or "MemoryLimitExceeded" or SystemError;

    public long Id { get; set; }
    public long UserId { get; set; }
    public long OlympiadId { get; set; }
    public long ProblemId { get; set; }
    public string Language { get; set; } = null!;
    public string SourceCode { get; set; } = null!;
    public string Status { get; set; } = Queued;
    public DateTime SubmittedAtUtc { get; set; }
    public string? JudgeWorkerId { get; set; }
    public Guid? JudgeLeaseToken { get; set; }
    public DateTime? JudgeLeaseExpiresAtUtc { get; set; }
    public int JudgeAttemptCount { get; set; }
    public DateTime? JudgeStartedAtUtc { get; set; }
    public DateTime? JudgeFinishedAtUtc { get; set; }
}

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using CodeRank.Api.Models;

namespace CodeRank.Api.Contracts.Judge;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class JudgeLeaseRequest : IValidatableObject
{
    [Required]
    public Guid? LeaseToken { get; init; }

    public virtual IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (LeaseToken == Guid.Empty) yield return new("Lease token must be nonempty.", [nameof(LeaseToken)]);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CompleteJudgeJobRequest : JudgeLeaseRequest
{
    [Required, StringLength(32)]
    public string Verdict { get; init; } = "";

    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var error in base.Validate(validationContext)) yield return error;
        if (!Submission.IsFinalVerdict(Verdict))
            yield return new("Verdict must be a final submission status.", [nameof(Verdict)]);
    }
}

public sealed record JudgeJobLease(long Id, long ProblemId, string Language, Guid LeaseToken, DateTime LeaseExpiresAtUtc);
public sealed record JudgeHeartbeat(DateTime LeaseExpiresAtUtc);

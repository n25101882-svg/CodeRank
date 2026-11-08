using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json.Serialization;
using CodeRank.Api.Models;
using CodeRank.Judge.Protocol;

namespace CodeRank.Api.Contracts.Submissions;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CreateSubmissionRequest : IValidatableObject
{
    public const int MaxSourceBytes = JudgePayloadContract.MaxSourceBytes;
    // Allow JSON escaping (up to six bytes per ASCII character) around a valid source.
    public const int MaxRequestBytes = 512 * 1024;

    [Required]
    public string Language { get; init; } = "";

    [Required]
    public string SourceCode { get; init; } = "";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Language is not (Submission.Cpp17 or Submission.Python3))
            yield return new("Choose cpp17 or python3.", [nameof(Language)]);
        if (Encoding.UTF8.GetByteCount(SourceCode) > MaxSourceBytes)
            yield return new("Source code must be at most 64 KiB in UTF-8.", [nameof(SourceCode)]);
    }
}

public sealed class SubmissionHistoryQuery
{
    public const int MaxPage = 1_000;

    [Range(1, MaxPage)]
    public int Page { get; init; } = 1;

    [Range(1, 50)]
    public int PageSize { get; init; } = 20;

    [Range(typeof(long), "1", "9223372036854775807")]
    public long? OlympiadId { get; init; }

    [Range(typeof(long), "1", "9223372036854775807")]
    public long? ProblemId { get; init; }

    [Required, RegularExpression("newest|oldest")]
    public string Sort { get; init; } = "newest";
}

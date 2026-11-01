using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CodeRank.Api.Contracts.Problems;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ProblemRequest
{
    [Required, StringLength(200)]
    public string Title { get; init => field = value?.Trim() ?? ""; } = "";

    [Required]
    public string Statement { get; init; } = "";

    public string? InputFormat { get; init; }
    public string? OutputFormat { get; init; }

    [AllowedValues(null, "Easy", "Medium", "Hard")]
    public string? Difficulty { get; init; }
}

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CodeRank.Api.Contracts.Olympiads;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AdminOlympiadRequest : IValidatableObject
{
    [Required, StringLength(200)]
    public string Title { get; init => field = value?.Trim() ?? ""; } = "";

    public string? Description { get; init; }

    [Required]
    public DateTime? StartsAtUtc { get; init; }

    [Required]
    public DateTime? EndsAtUtc { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (StartsAtUtc is { Kind: not DateTimeKind.Utc })
            yield return new("Start time must be UTC with a Z suffix.", [nameof(StartsAtUtc)]);
        if (EndsAtUtc is { Kind: not DateTimeKind.Utc })
            yield return new("End time must be UTC with a Z suffix.", [nameof(EndsAtUtc)]);
        if (StartsAtUtc is { } start && EndsAtUtc is { } end && end <= start)
            yield return new("End time must be after the start time.", [nameof(EndsAtUtc)]);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AdminOlympiadPublicationRequest
{
    [Required]
    public bool? IsPublished { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AdminOlympiadLinksRequest : IValidatableObject
{
    [Required, RegularExpression("^[A-F0-9]{64}$")]
    public string Revision { get; init; } = "";

    [Required]
    public List<AdminOlympiadLinkRequest?> Items { get; init; } = null!;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Items?.Any(item => item is null) == true)
            yield return new("Each item must be a problem link.", [nameof(Items)]);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AdminOlympiadLinkRequest
{
    [Range(typeof(long), "1", "9223372036854775807")]
    public long ProblemId { get; init; }

    [Range(1, int.MaxValue)]
    public int Position { get; init; }

    [Range(1, int.MaxValue)]
    public int MaxPoints { get; init; }

    [Required]
    public bool? IsVisible { get; init; }
}

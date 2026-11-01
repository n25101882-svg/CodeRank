using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using CodeRank.Api.Validation;

namespace CodeRank.Api.Contracts.Auth;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RegisterRequest
{
    [Required, EmailAddress, StringLength(256)]
    public string Email { get; init => field = value?.Trim() ?? ""; } = "";

    [Required, StringLength(128, MinimumLength = 12), RegistrationPassword]
    public string Password { get; init; } = "";

    [Required, StringLength(100), UnicodeName]
    public string FirstName { get; init; } = "";

    [Required, StringLength(100), UnicodeName]
    public string LastName { get; init; } = "";
}

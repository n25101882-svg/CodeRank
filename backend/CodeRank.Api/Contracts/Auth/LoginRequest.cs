using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CodeRank.Api.Contracts.Auth;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class LoginRequest
{
    [Required, EmailAddress, StringLength(256)]
    public string Email { get; init => field = value?.Trim() ?? ""; } = "";

    [Required, StringLength(128)]
    public string Password { get; init; } = "";
}

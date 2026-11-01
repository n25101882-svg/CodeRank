namespace CodeRank.Api.Contracts.Auth;

public sealed record UserResponse(long Id, string Email, string? FirstName, string? LastName, string Role);
public sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresAtUtc, string TokenType = "Bearer");

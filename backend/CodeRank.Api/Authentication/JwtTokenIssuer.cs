using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using CodeRank.Api.Contracts.Auth;
using CodeRank.Api.Models;
using Microsoft.IdentityModel.Tokens;

namespace CodeRank.Api.Authentication;

public sealed class JwtTokenIssuer(JwtSettings settings, TimeProvider clock)
{
    public LoginResponse Issue(User user)
    {
        var now = clock.GetUtcNow();
        // JWT uses whole seconds; return the exact expiry present in the token.
        var expires = DateTimeOffset.FromUnixTimeSeconds(now.Add(settings.Lifetime).ToUnixTimeSeconds());
        var token = new JwtSecurityToken(settings.Issuer, settings.Audience,
            [new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString(CultureInfo.InvariantCulture)),
             new Claim("role", user.Role),
             new Claim(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
             new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))],
            notBefore: now.UtcDateTime, expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(settings.SigningKey, SecurityAlgorithms.HmacSha256));
        return new(new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}

using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using CodeRank.Api.Authentication;
using CodeRank.Api.Contracts.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CodeRank.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/users")]
public sealed class UsersController(AuthService auth) : ControllerBase
{
    [HttpGet("me")]
    public async Task<ActionResult<UserResponse>> Me(CancellationToken token)
    {
        if (!long.TryParse(User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value,
            NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0) return Unauthorized();
        var user = await auth.CurrentUserAsync(id, token);
        return user is null ? Unauthorized() : Ok(user);
    }
}

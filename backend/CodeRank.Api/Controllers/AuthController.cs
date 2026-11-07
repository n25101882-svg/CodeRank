using CodeRank.Api.Authentication;
using CodeRank.Api.Contracts.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CodeRank.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(AuthService auth) : ControllerBase
{
    [HttpPost("register")]
    [AllowAnonymous]
    [RequestSizeLimit(16 * 1024)]
    public async Task<ActionResult<UserResponse>> Register(RegisterRequest request, CancellationToken token)
    {
        var user = await auth.RegisterAsync(request, token);
        if (user is null) return Conflict(new ProblemDetails { Status = 409, Title = "Email is already registered." });
        return Created("/api/v1/users/me", user);
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(LoginRateLimitPolicy.Name)]
    [RequestSizeLimit(16 * 1024)]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request, CancellationToken token)
    {
        var result = await auth.LoginAsync(request, token);
        if (result is null) return Unauthorized(new ProblemDetails { Status = 401, Title = "Invalid credentials." });
        return Ok(result);
    }
}

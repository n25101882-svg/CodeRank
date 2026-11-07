using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using CodeRank.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CodeRank.Api.Authentication;

public sealed class ActiveAdminRequirement : IAuthorizationRequirement
{
    public const string Policy = "ActiveAdmin";
    public const string Role = "Admin";

    public static long? Subject(ClaimsPrincipal user) =>
        long.TryParse(user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value,
            NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
}

public sealed class ActiveAdminHandler(CodeRankDbContext db) : AuthorizationHandler<ActiveAdminRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ActiveAdminRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true ||
            ActiveAdminRequirement.Subject(context.User) is not long userId)
            return;

        var token = (context.Resource as HttpContext)?.RequestAborted ?? CancellationToken.None;
        var role = await db.Users.AsNoTracking()
            .Where(user => user.Id == userId && user.IsActive)
            .Select(user => user.Role)
            .SingleOrDefaultAsync(token);
        if (role == ActiveAdminRequirement.Role)
            context.Succeed(requirement);
    }
}

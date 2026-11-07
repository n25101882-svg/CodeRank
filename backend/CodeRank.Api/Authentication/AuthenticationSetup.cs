using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace CodeRank.Api.Authentication;

public static class AuthenticationSetup
{
    public static IServiceCollection AddCodeRankAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IJwtSigningKeySource, LocalJwtSigningKeySource>();
        services.AddSingleton(sp => JwtSettings.Load(configuration, sp.GetRequiredService<IJwtSigningKeySource>()));
        services.AddSingleton<JwtTokenIssuer>();
        services.AddScoped<AuthService>();
        services.AddSingleton<IRegistrationConflictDetector, SqlServerRegistrationConflictDetector>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<JwtSettings>((options, jwt) =>
        {
            options.MapInboundClaims = false;
            options.IncludeErrorDetails = false;
            options.SaveToken = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true, IssuerSigningKey = jwt.SigningKey,
                RequireSignedTokens = true, ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ValidateIssuer = true, ValidIssuer = jwt.Issuer,
                ValidateAudience = true, ValidAudience = jwt.Audience,
                ValidateLifetime = true, RequireExpirationTime = true, ClockSkew = TimeSpan.Zero,
                NameClaimType = "sub", RoleClaimType = "role"
            };
        });
        services.AddAuthorization(options => options.AddPolicy(ActiveAdminRequirement.Policy,
            policy => policy.RequireAuthenticatedUser().AddRequirements(new ActiveAdminRequirement())));
        services.AddScoped<IAuthorizationHandler, ActiveAdminHandler>();
        return services;
    }
}

using Microsoft.IdentityModel.Tokens;

namespace CodeRank.Api.Authentication;

public interface IJwtSigningKeySource
{
    string? Read();
}

/// <summary>Signing secrets deliberately exclude appsettings and command-line providers.</summary>
public sealed class LocalJwtSigningKeySource(IHostEnvironment environment) : IJwtSigningKeySource
{
    public string? Read()
    {
        var builder = new ConfigurationBuilder();
        if (environment.IsDevelopment())
            builder.AddUserSecrets(typeof(LocalJwtSigningKeySource).Assembly, optional: true);
        var privateConfig = builder.AddEnvironmentVariables().Build();
        try { return privateConfig["Jwt:SigningKey"]; }
        finally { (privateConfig as IDisposable)?.Dispose(); }
    }
}

public sealed class JwtConfigurationException() : Exception(
    "JWT configuration is missing or invalid. Configure Jwt:Issuer, Jwt:Audience, " +
    "Jwt:AccessTokenMinutes (1-60) and private Jwt:SigningKey (Base64, at least 32 random bytes).") { }

public sealed class JwtSettings
{
    public required string Issuer { get; init; }
    public required string Audience { get; init; }
    public required TimeSpan Lifetime { get; init; }
    public required SymmetricSecurityKey SigningKey { get; init; }

    public static JwtSettings Load(IConfiguration configuration, IJwtSigningKeySource keySource)
    {
        var issuer = configuration["Jwt:Issuer"];
        var audience = configuration["Jwt:Audience"];
        if (string.IsNullOrWhiteSpace(issuer) || issuer.Length > 256 ||
            string.IsNullOrWhiteSpace(audience) || audience.Length > 256 ||
            !int.TryParse(configuration["Jwt:AccessTokenMinutes"], out var minutes) || minutes is < 1 or > 60)
            throw new JwtConfigurationException();
        byte[] key;
        try { key = Convert.FromBase64String(keySource.Read() ?? ""); }
        catch (FormatException) { throw new JwtConfigurationException(); }
        if (key.Length is < 32 or > 128) throw new JwtConfigurationException();
        return new JwtSettings { Issuer = issuer, Audience = audience, Lifetime = TimeSpan.FromMinutes(minutes),
            SigningKey = new SymmetricSecurityKey(key) };
    }
}

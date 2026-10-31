namespace CodeRank.Api.Data.Seeding;

public interface ISeedPasswordSource
{
    string? GetPassword(string key);
}

/// <summary>Only User Secrets (Development) and process environment; never appsettings or CLI arguments.</summary>
public sealed class LocalSeedPasswordSource(IHostEnvironment environment) : ISeedPasswordSource, IDisposable
{
    private IConfigurationRoot? configuration;

    public string? GetPassword(string key)
    {
        if (configuration is null)
        {
            var builder = new ConfigurationBuilder();
            if (environment.IsDevelopment())
                builder.AddUserSecrets(typeof(LocalSeedPasswordSource).Assembly, optional: true);
            configuration = builder.AddEnvironmentVariables().Build();
        }
        return configuration[key];
    }

    public void Dispose() => (configuration as IDisposable)?.Dispose();
}

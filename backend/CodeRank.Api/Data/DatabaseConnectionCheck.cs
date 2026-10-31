using Microsoft.EntityFrameworkCore;

namespace CodeRank.Api.Data;

/// <summary>A local, read-only connection check; never creates or migrates the database.</summary>
internal static class DatabaseConnectionCheck
{
    public static async Task<int> RunAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var db = scope.ServiceProvider.GetRequiredService<CodeRankDbContext>();
            if (await db.Database.CanConnectAsync(timeout.Token))
            {
                Console.WriteLine("Database connection successful. No schema or data changes were made.");
                return 0;
            }
        }
        catch (Exception)
        {
            // Do not print connection strings, credentials or provider exception details.
        }

        Console.Error.WriteLine("Database connection failed. Check local configuration, server access and login permissions.");
        return 1;
    }
}

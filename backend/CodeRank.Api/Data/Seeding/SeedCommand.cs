namespace CodeRank.Api.Data.Seeding;

public sealed record SeedCommandOptions(bool Requested, bool DryRun, string? TargetDatabase, string[] HostArguments)
{
    public static SeedCommandOptions Parse(string[] args)
    {
        var malformed = args.Any(a =>
            (a.StartsWith("--seed", StringComparison.OrdinalIgnoreCase)
             || a.StartsWith("--apply", StringComparison.OrdinalIgnoreCase)
             || a.StartsWith("--dry-run", StringComparison.OrdinalIgnoreCase))
            && a is not ("--seed" or "--apply" or "--dry-run")
            && !a.StartsWith("--seed-target=", StringComparison.Ordinal));
        if (malformed)
            throw new SeedException("Seed switches must use the documented exact forms; do not pass passwords on the command line.");
        var requested = args.Contains("--seed", StringComparer.Ordinal);
        var apply = args.Contains("--apply", StringComparer.Ordinal);
        var preview = args.Contains("--dry-run", StringComparer.Ordinal);
        var targets = args.Where(a => a.StartsWith("--seed-target=", StringComparison.Ordinal)).ToArray();
        if ((!requested && (apply || preview || targets.Length > 0)) || (apply && preview) || targets.Length > 1)
            throw new SeedException("Use --seed [--dry-run] OR --seed --apply --seed-target=<database>.");
        var target = targets.SingleOrDefault()?["--seed-target=".Length..];
        if (apply && string.IsNullOrWhiteSpace(target))
            throw new SeedException("Write mode requires --seed-target=<database>.");
        var hostArgs = args.Where(a => a is not ("--seed" or "--apply" or "--dry-run")
            && !a.StartsWith("--seed-target=", StringComparison.Ordinal)).ToArray();
        return new(requested, !apply, target, hostArgs);
    }
}

internal static class SeedCommand
{
    public static async Task<int> RunAsync(IServiceProvider services, SeedCommandOptions options)
    {
        using var scope = services.CreateScope();
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<DatabaseSeeder>>();
        try
        {
            await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>()
                .RunAsync(options.DryRun, options.TargetDatabase, cancellation.Token);
            return 0;
        }
        catch (SeedException error)
        {
            logger.LogError("Seeder stopped: {Reason}", error.Message);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Seeder cancelled.");
        }
        catch (Microsoft.Data.SqlClient.SqlException error)
        {
            logger.LogError("Seed {Mode} stopped with SQL error {ErrorNumber}. Check permissions/constraints before retrying.", options.DryRun ? "dry-run" : "apply", error.Number);
        }
        catch (Exception error)
        {
            // Never log provider exception details, credentials, hashes, or connection strings.
            logger.LogError("Seeder failed ({ErrorType}). Check database access/configuration and review state before retrying.", error.GetType().Name);
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
        return 1;
    }
}

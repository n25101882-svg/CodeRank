namespace CodeRank.Api.Data.Seeding;

/// <summary>Contains only a safe diagnostic, never a secret or a provider exception.</summary>
public sealed class SeedException(string message) : Exception(message);

public sealed record SeedSummary(bool DryRun, IReadOnlyDictionary<string, int> Created,
    IReadOnlyDictionary<string, int> Reused);

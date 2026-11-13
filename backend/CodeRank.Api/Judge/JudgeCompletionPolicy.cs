using CodeRank.Judge.Protocol;

namespace CodeRank.Api.Judge;

public sealed class JudgeCompletionPolicy
{
    private static readonly JudgeCompletionPolicy Disabled = new([]);
    private readonly HashSet<string> allowedWorkers;

    private JudgeCompletionPolicy(HashSet<string> allowedWorkers) => this.allowedWorkers = allowedWorkers;

    public static JudgeCompletionPolicy Load(IConfiguration configuration, JudgeWorkerSettings workers)
    {
        var completion = configuration.GetSection("Judge:Completion");
        if (!bool.TryParse(completion["Enabled"], out var enabled) || !enabled) return Disabled;

        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in completion.GetSection("Workers").GetChildren())
        {
            var workerId = entry.Value;
            if (!WorkerCredentials.IsValidId(workerId) || !workers.IsConfigured(workerId!) || !allowed.Add(workerId!))
                return Disabled;
        }
        return allowed.Count == 0 ? Disabled : new(allowed);
    }

    public bool Allows(string workerId) => allowedWorkers.Contains(workerId);
}

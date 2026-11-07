using System.Security.Cryptography;
using CodeRank.Judge.Protocol;

namespace CodeRank.Api.Judge;

public sealed class JudgeConfigurationException() : Exception(
    "Judge API requires at least one worker and unique private Base64 keys of 32 random bytes. " +
    "Worker IDs use 1-64 lowercase ASCII letters and digits.") { }

public sealed class JudgeWorkerSettings
{
    private readonly record struct Keys(byte[] Current, byte[]? Previous);
    private static readonly byte[] DummyKey = RandomNumberGenerator.GetBytes(WorkerCredentials.KeyLength);
    private readonly Dictionary<string, Keys> workers;

    private JudgeWorkerSettings(Dictionary<string, Keys> workers) => this.workers = workers;

    public static JudgeWorkerSettings Load(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!configuration.GetValue<bool>("Judge:Enabled")) return new([]);

        // Worker secrets use the same private providers as JWT signing keys.
        var builder = new ConfigurationBuilder();
        if (environment.IsDevelopment())
            builder.AddUserSecrets(typeof(JudgeWorkerSettings).Assembly, optional: true);
        var privateConfig = builder.AddEnvironmentVariables().Build();
        try { return FromPrivateConfiguration(privateConfig); }
        finally { (privateConfig as IDisposable)?.Dispose(); }
    }

    public static JudgeWorkerSettings FromPrivateConfiguration(IConfiguration configuration)
    {
        var workers = new Dictionary<string, Keys>(StringComparer.Ordinal);
        var usedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var worker in configuration.GetSection("Judge:Workers").GetChildren())
        {
            if (!WorkerCredentials.IsValidId(worker.Key)) throw new JudgeConfigurationException();
            var current = DecodeKey(worker["CurrentKey"], usedKeys);
            var previous = worker["PreviousKey"] is { } value ? DecodeKey(value, usedKeys) : null;
            workers.Add(worker.Key, new Keys(current, previous));
        }
        if (workers.Count == 0) throw new JudgeConfigurationException();
        return new(workers);
    }

    internal bool Authenticate(string workerId, string? key)
    {
        Span<byte> supplied = stackalloc byte[WorkerCredentials.KeyLength];
        if (!WorkerCredentials.TryDecodeKey(key, supplied)) return false;

        var found = workers.TryGetValue(workerId, out var keys);
        var currentMatch = CryptographicOperations.FixedTimeEquals(keys.Current ?? DummyKey, supplied);
        var previousMatch = CryptographicOperations.FixedTimeEquals(keys.Previous ?? DummyKey, supplied);
        CryptographicOperations.ZeroMemory(supplied);
        return found & (currentMatch | (keys.Previous is not null & previousMatch));
    }

    private static byte[] DecodeKey(string? value, HashSet<string> usedKeys)
    {
        var key = new byte[WorkerCredentials.KeyLength];
        if (!WorkerCredentials.TryDecodeKey(value, key) || !usedKeys.Add(value!)) throw new JudgeConfigurationException();
        return key;
    }
}

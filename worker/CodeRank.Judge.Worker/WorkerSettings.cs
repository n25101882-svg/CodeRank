using CodeRank.Judge.Protocol;

namespace CodeRank.Judge.Worker;

internal sealed class WorkerConfigurationException() : Exception(
    "Set JudgeWorker__ApiBaseUrl to an HTTPS API base URL and configure JudgeWorker__WorkerId and JudgeWorker__Key. " +
    "Set JudgeWorker__FetchPayload and JudgeWorker__EvaluateSubmissions to true or false. " +
    "Evaluation requires payload fetching.") { }

internal sealed class WorkerSettings(Uri baseAddress, string workerId, string key)
{
    private static readonly string DefaultDockerExecutable = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "resources", "bin", "docker.exe");

    public Uri BaseAddress { get; } = baseAddress;
    public string WorkerId { get; } = workerId;
    public string Key { get; } = key;
    public bool FetchPayload { get; init; }
    public bool EvaluateSubmissions { get; init; }
    public string DockerExecutable { get; init; } = DefaultDockerExecutable;
    public TimeSpan IdleInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    public static WorkerSettings Load(IConfiguration privateConfiguration)
    {
        var config = privateConfiguration.GetSection("JudgeWorker");
        var workerId = config["WorkerId"];
        var key = config["Key"];
        var dockerExecutable = config["DockerExecutable"];
        if (!Uri.TryCreate(config["ApiBaseUrl"], UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Host.Length == 0 ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !WorkerCredentials.IsValidId(workerId) ||
            !WorkerCredentials.TryDecodeKey(key, stackalloc byte[WorkerCredentials.KeyLength]) ||
            !bool.TryParse(config["FetchPayload"] ?? "false", out var fetchPayload) ||
            !bool.TryParse(config["EvaluateSubmissions"] ?? "false", out var evaluate) || (evaluate && !fetchPayload) ||
            (evaluate && dockerExecutable is not null && !IsLocalDockerExecutable(dockerExecutable)))
            throw new WorkerConfigurationException();

        return new WorkerSettings(new Uri(uri.AbsoluteUri.TrimEnd('/') + "/"), workerId!, key!)
        {
            FetchPayload = fetchPayload,
            EvaluateSubmissions = evaluate,
            DockerExecutable = dockerExecutable ?? DefaultDockerExecutable
        };
    }

    private static bool IsLocalDockerExecutable(string path) =>
        Path.IsPathFullyQualified(path) && Path.GetPathRoot(path) is { Length: 3 } root && root[1] == ':' &&
        string.Equals(Path.GetFileName(path), "docker.exe", StringComparison.OrdinalIgnoreCase);
}

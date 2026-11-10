using CodeRank.Judge.Worker;

var builder = Host.CreateApplicationBuilder(args);
using var privateConfiguration = new ConfigurationManager();
if (builder.Environment.IsDevelopment())
    privateConfiguration.AddUserSecrets<WorkerSettings>(optional: true);
privateConfiguration.AddEnvironmentVariables();

WorkerSettings settings;
try { settings = WorkerSettings.Load(privateConfiguration); }
catch (WorkerConfigurationException error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

using var dockerWorkspace = settings.EvaluateSubmissions ? OpenDockerWorkspace(settings.WorkerId) : null;
if (settings.EvaluateSubmissions && dockerWorkspace is null) return 1;

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
{
    Timeout = TimeSpan.FromSeconds(10),
    MaxResponseContentBufferSize = 8 * 1024
});
builder.Services.AddSingleton<JudgeApiClient>();
if (dockerWorkspace is not null)
{
    // Shutdown must allow an in-flight create and the bounded removal/verification commands to finish.
    builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(75));
    builder.Services.AddSingleton(dockerWorkspace);
    builder.Services.AddSingleton<IDockerCli>(new DockerCli(settings.DockerExecutable, dockerWorkspace.DirectoryPath));
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<IIsolatedJudgeRunner, DockerExecutionProvider>();
    builder.Services.AddSingleton<SubmissionGrader>();
    builder.Services.AddSingleton<LeasedSubmissionJudge>();
}
builder.Services.AddHostedService<JudgeWorker>();

using var host = builder.Build();
await host.RunAsync();
return 0;

static DockerWorkspace? OpenDockerWorkspace(string workerId)
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("Docker Judge evaluation requires Windows with Docker Desktop Linux Engine.");
        return null;
    }
    try
    {
        return DockerWorkspace.Open(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), workerId);
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
    {
        Console.Error.WriteLine("Docker Judge workspace unavailable. Check its session lock and pending-container recovery marker.");
        return null;
    }
}

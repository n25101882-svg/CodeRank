using System.Runtime.InteropServices;
using CodeRank.Judge.Protocol;
using CodeRank.Judge.Worker;

var builder = Host.CreateApplicationBuilder(args);
using var privateConfiguration = new ConfigurationManager();
privateConfiguration.AddEnvironmentVariables();

WorkerSettings settings;
try { settings = WorkerSettings.Load(privateConfiguration); }
catch (WorkerConfigurationException error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
{
    Timeout = TimeSpan.FromSeconds(10),
    MaxResponseContentBufferSize = 8 * 1024
});
builder.Services.AddSingleton<JudgeApiClient>();
if (settings.EvaluateSubmissions)
{
    try
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            LinuxJudgeIdentity.GetEffectiveUid() != LinuxJudgeIdentity.ReadServiceUid(JudgeIpc.WorkerAccount))
            throw new InvalidOperationException();
    }
    catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine("Evaluation requires the dedicated Judge Worker service user on Linux x64.");
        return 1;
    }
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<IIsolatedJudgeRunner, UnixJudgeRunner>();
    builder.Services.AddSingleton<SubmissionGrader>();
    builder.Services.AddSingleton<LeasedSubmissionJudge>();
}
builder.Services.AddHostedService<JudgeWorker>();

using var host = builder.Build();
await host.RunAsync();
return 0;

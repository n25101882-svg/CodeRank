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
builder.Services.AddHostedService<JudgeWorker>();

using var host = builder.Build();
await host.RunAsync();
return 0;

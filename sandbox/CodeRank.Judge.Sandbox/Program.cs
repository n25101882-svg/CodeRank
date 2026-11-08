using System.Runtime.InteropServices;
using CodeRank.Judge.Sandbox;
using CodeRank.Judge.Worker;

if (args is not (["--integration"] or ["--expect-unavailable"]))
{
    Console.Error.WriteLine("Usage: CodeRank.Judge.Sandbox --integration | --expect-unavailable");
    return 2;
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stop.Cancel();
};
using var terminate = OperatingSystem.IsLinux() ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    stop.Cancel();
}) : null;

try
{
    if (args[0] == "--expect-unavailable")
    {
        // A synthetic negative test. A missing real prerequisite must stop the payload before execution.
        var negative = new NsJailRunner(new LinuxSandboxExecutor(), new SandboxLimits(), ReportFailure);
        var failed = await negative.RunAsync("python3", "print('unexpected execution')", [""], stop.Token);
        var refused = failed is [{ Status: IsolatedCaseStatus.SystemError, StandardOutput: "" }];
        Console.WriteLine(refused ? "PASS isolation prerequisite refusal" : "FAIL isolation prerequisite refusal");
        return refused ? 0 : 1;
    }

    var limits = LinuxSandboxHost.LoadLimits();
    var runner = new NsJailRunner(new LinuxSandboxExecutor(), limits, ReportFailure);
    return await LinuxSmokeTests.RunAsync(runner, limits, stop.Token);
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
    return 130;
}
catch (Exception error) when (SandboxFailure.IsInfrastructureError(error))
{
    ReportFailure(SandboxFailure.From(SandboxStage.Configuration, error));
    return 1;
}

static void ReportFailure(SandboxFailure failure) => Console.Error.WriteLine(failure.ToLogLine());

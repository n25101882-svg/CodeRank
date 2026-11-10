using CodeRank.Judge.Protocol;
using CodeRank.Judge.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodeRank.Judge.Worker.Tests;

public sealed class DockerDesktopQaTests
{
    private const string WorkerId = "coderankdockersmoke";
    private const string Cpp = "#include <iostream>\nint main() { std::cout << 2 + 2 << '\\n'; }";
    private const string BusyLoop = "while True: pass";
    private const string QaOnly = "Opt in on a Windows QA workstation with Docker Desktop and trusted local images.";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public static bool Enabled => OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("CODERANK_DOCKER_QA") == "1";

    [Theory(Skip = QaOnly, SkipUnless = nameof(Enabled))]
    [InlineData("python3", "print(2 + 2)", "4", "Accepted")]
    [InlineData("cpp17", Cpp, "4", "Accepted")]
    [InlineData("python3", "print(4)", "5", "WrongAnswer")]
    [InlineData("cpp17", "int main( {", "", "CompilationError")]
    [InlineData("python3", "raise ValueError('synthetic')", "", "RuntimeError")]
    [InlineData("python3", BusyLoop, "", "TimeLimitExceeded")]
    [InlineData("python3", "data = bytearray(512 * 1024 * 1024)", "", "MemoryLimitExceeded")]
    public async Task Synthetic_programs_use_the_real_Desktop_engine(string language, string source, string expected, string verdict)
    {
        using var qa = new Session();
        var grader = new SubmissionGrader(qa.Provider);
        Assert.Equal(verdict, await grader.GradeAsync(Payload(language, source, expected), Token));
        await qa.AssertNoContainersAsync();
    }

    [Fact(Skip = QaOnly, SkipUnless = nameof(Enabled))]
    public async Task Five_sequential_cases_obey_the_container_policy_and_leave_no_resources()
    {
        const string isolation = """
            import os, socket
            assert (os.getuid(), os.getgid()) == (65534, 65534)
            assert os.statvfs('/').f_flag & os.ST_RDONLY
            assert not os.path.exists('/var/run/docker.sock')
            assert 'JudgeWorker__Key' not in os.environ
            assert {name for _, name in socket.if_nameindex()} <= {'lo'}
            status = dict(line.split(':', 1) for line in open('/proc/self/status') if ':' in line)
            assert int(status['CapEff'], 16) == 0 and int(status['NoNewPrivs']) == 1
            print(input())
            """;
        using var qa = new Session();
        var inputs = new[] { "one\n", "two\n", "three\n", "four\n", "five\n" };
        var results = await qa.Provider.RunAsync("python3", isolation, inputs, Token);
        Assert.All(results, result => Assert.Equal(IsolatedCaseStatus.Exited, result.Status));
        Assert.Equal(inputs, results.Select(result => result.StandardOutput));
        await qa.AssertNoContainersAsync();
    }

    [Fact(Skip = QaOnly, SkipUnless = nameof(Enabled))]
    public async Task Cancelling_an_active_Desktop_container_removes_it()
    {
        using var qa = new Session();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var run = qa.Provider.RunAsync("python3", BusyLoop, [""], cancel.Token);
        bool running;
        var cancelled = false;
        try { running = await qa.WaitForRunningAsync(run); }
        finally
        {
            await cancel.CancelAsync();
            try { await run; }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { cancelled = true; }
        }
        Assert.True(cancelled);
        Assert.True(running, "The QA observer must see a running container before cancellation.");
        await qa.AssertNoContainersAsync();
    }

    [Fact(Skip = QaOnly, SkipUnless = nameof(Enabled))]
    public async Task A_lost_lease_cancels_the_Desktop_container_before_allowing_another_claim()
    {
        using var qa = new Session();
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var api = new LeasedSubmissionJudgeTests.Harness(Payload("python3", BusyLoop, ""))
        {
            OnHeartbeat = (_, ok) => lost.Task.IsCompleted ? LeasedSubmissionJudgeTests.Lost() : ok()
        };
        var run = api.RunAsync(qa.Provider, TimeProvider.System, TimeSpan.FromMilliseconds(20));
        bool running;
        LeasedJudgeResult outcome;
        try { running = await qa.WaitForRunningAsync(run); }
        finally
        {
            lost.TrySetResult();
            outcome = await run;
        }
        Assert.Equal(LeasedJudgeResult.LeaseEnded, outcome);
        Assert.True(running, "The QA observer must see a running container before lease loss.");
        Assert.Empty(api.SavedVerdicts);
        await qa.AssertNoContainersAsync();
    }

    private static JudgePayload Payload(string language, string source, string expected) => new()
    {
        Id = 1, ProblemId = 1, Language = language, SourceCode = source,
        TestCases = [new() { Id = 1, Position = 1, Input = "", ExpectedOutput = expected }]
    };

    private sealed class Session : IDisposable
    {
        private readonly DockerWorkspace workspace;
        private readonly ObservedCli cli;
        public DockerExecutionProvider Provider { get; }

        public Session()
        {
            var settings = new WorkerSettings(new Uri("https://qa.example.test/"), WorkerId, "");
            var executable = Environment.GetEnvironmentVariable("JudgeWorker__DockerExecutable") ?? settings.DockerExecutable;
            workspace = DockerWorkspace.Open(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), WorkerId);
            cli = new(new DockerCli(executable, workspace.DirectoryPath));
            Provider = new(cli, workspace, settings, NullLogger<DockerExecutionProvider>.Instance);
        }

        public async Task<bool> WaitForRunningAsync(Task evaluation)
        {
            if (await Task.WhenAny(cli.Started.Task, evaluation) == evaluation) return false;
            var id = await cli.Started.Task;
            while (!evaluation.IsCompleted)
            {
                var state = await cli.RunAsync(new(["container", "inspect", "--format", "{{.State.Running}}", id],
                    TimeSpan.FromSeconds(2)), Token);
                if (state.Succeeded && System.Text.Encoding.ASCII.GetString(state.Output).Trim() == "true") return true;
                await Task.Delay(20, Token);
            }
            return false;
        }

        public async Task AssertNoContainersAsync()
        {
            var state = await cli.RunAsync(new(["container", "ls", "--all", "--quiet", "--filter",
                $"label={DockerExecutionProvider.WorkerLabel}={WorkerId}"], TimeSpan.FromSeconds(10)), Token);
            Assert.True(state.Succeeded);
            Assert.True(string.IsNullOrWhiteSpace(System.Text.Encoding.ASCII.GetString(state.Output)));
            Assert.False(File.Exists(Path.Combine(workspace.DirectoryPath, "pending-container")));
        }

        public void Dispose() => workspace.Dispose();
    }

    private sealed class ObservedCli(DockerCli cli) : IDockerCli
    {
        public TaskCompletionSource<string> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DockerCommandResult> RunAsync(DockerCommand command, CancellationToken token)
        {
            if (command.Arguments is ["container", "start", _, _, var id]) Started.TrySetResult(id);
            return cli.RunAsync(command, token);
        }
    }
}

using System.Text;
using CodeRank.Judge.Sandbox;
using CodeRank.Judge.Worker;
using Xunit;

namespace CodeRank.Judge.Sandbox.Tests;

public sealed class NsJailRunnerTests
{
    [Fact]
    public async Task Cpp_compiles_once_and_only_the_current_input_is_sent_to_each_invocation()
    {
        var artifact = Elf();
        var executor = new RecordingExecutor((call, _) => Task.FromResult(new SandboxExecution(IsolatedCaseStatus.Exited,
            call.Phase == SandboxPhase.Compile ? artifact : call.Input)));
        var runner = new NsJailRunner(executor, new SandboxLimits());
        var results = await runner.RunAsync("cpp17", "int main() {}", ["one", "two"], Token);
        Assert.Equal(new[] { "one", "two" }, results.Select(result => result.StandardOutput));
        Assert.Equal(new[] { SandboxPhase.Compile, SandboxPhase.Cpp17, SandboxPhase.Cpp17 },
            executor.Calls.Select(call => call.Phase));
        Assert.Empty(executor.Calls[0].Input);
        Assert.Equal("int main() {}", Encoding.UTF8.GetString(executor.Calls[0].Payload));
        Assert.All(executor.Calls.Skip(1), call => Assert.Same(artifact, call.Payload));
        Assert.Equal(new[] { "one", "two" }, executor.Calls.Skip(1).Select(call => Encoding.UTF8.GetString(call.Input)));
    }

    [Fact]
    public async Task Compilation_failure_returns_without_running_any_case()
    {
        var executor = new RecordingExecutor((_, _) => Task.FromResult(new SandboxExecution(IsolatedCaseStatus.CompilationError, [])));
        var runner = new NsJailRunner(executor, new SandboxLimits());
        var results = await runner.RunAsync("cpp17", "not C++", ["one", "two"], Token);
        Assert.All(results, result => Assert.Equal(IsolatedCaseStatus.CompilationError, result.Status));
        Assert.Equal(SandboxPhase.Compile, Assert.Single(executor.Calls).Phase);
    }

    [Fact]
    public async Task Streamed_session_limits_inputs_even_when_each_frame_is_valid()
    {
        var executor = new RecordingExecutor((_, _) => Task.FromResult(new SandboxExecution(IsolatedCaseStatus.Exited, [])));
        var runner = new NsJailRunner(executor, new SandboxLimits());
        using var session = await runner.OpenAsync("python3", "synthetic", Token);
        for (var i = 0; i < 8; i++)
            Assert.Equal(IsolatedCaseStatus.Exited, (await session.RunCaseAsync(new byte[8192], Token)).Status);

        await Assert.ThrowsAsync<InvalidDataException>(() => session.RunCaseAsync([1], Token));
        Assert.Equal(8, executor.Calls.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Peer_disconnect_or_pipelined_frame_cancels_active_work(int bytes)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peerActivity = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = false;
        var run = SandboxIpcServer.WhileConnectedAsync<int>(ct => new(peerActivity.Task.WaitAsync(ct)), async ct =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { canceled = ct.IsCancellationRequested; }
            return 1;
        }, Token);

        await started.Task.WaitAsync(Token);
        peerActivity.SetResult(bytes);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.True(canceled);
    }

    [Theory]
    [InlineData("python3 --config=/tmp/unsafe", "print(1)", 1)]
    [InlineData("python3", "", 1)]
    [InlineData("python3", "print(1)", 0)]
    [InlineData("python3", "print(1)", 33)]
    public async Task Malformed_inputs_cannot_start_a_process(string language, string source, int cases)
    {
        var executor = new RecordingExecutor((_, _) => throw new InvalidOperationException("Unexpected process."));
        var runner = new NsJailRunner(executor, new SandboxLimits());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.RunAsync(language, source, Enumerable.Repeat("", cases).ToArray(), Token));
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public async Task Oversized_or_non_utf8_data_is_rejected_before_launch()
    {
        var executor = new RecordingExecutor((_, _) => throw new InvalidOperationException("Unexpected process."));
        var runner = new NsJailRunner(executor, new SandboxLimits());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.RunAsync("python3", new string('\u20ac', 22_000), [""], Token));
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync("python3", "print(1)", ["\ud800"], Token));
        Assert.Empty(executor.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Infrastructure_failure_is_latched_without_retry(bool invalidArtifact)
    {
        var executor = new RecordingExecutor((_, _) => invalidArtifact
            ? Task.FromResult(new SandboxExecution(IsolatedCaseStatus.Exited, "not an ELF"u8.ToArray()))
            : throw new SandboxUnavailableException());
        var runner = new NsJailRunner(executor, new SandboxLimits());
        var language = invalidArtifact ? "cpp17" : "python3";
        var results = await runner.RunAsync(language, "synthetic code", ["1", "2"], Token);
        Assert.All(results, result => Assert.Equal(IsolatedCaseStatus.SystemError, result.Status));
        Assert.Equal(IsolatedCaseStatus.SystemError,
            Assert.Single(await runner.RunAsync(language, "synthetic code", [""], Token)).Status);
        Assert.Single(executor.Calls);
    }

    [Fact]
    public async Task Runner_reports_first_system_error_once_and_never_logs_exception_or_input_text()
    {
        var diagnostics = new List<string>();
        var executor = new RecordingExecutor((_, _) => throw new IOException("private-exception-canary"));
        var runner = new NsJailRunner(executor, new SandboxLimits(), failure => diagnostics.Add(failure.ToLogLine()));
        var first = await runner.RunAsync("python3", "# private-source-canary", ["private-test-canary", ""], Token);
        var next = await runner.RunAsync("python3", "# second source", [""], Token);

        Assert.All(first.Concat(next), result =>
        {
            Assert.Equal(IsolatedCaseStatus.SystemError, result.Status);
            Assert.Empty(result.StandardOutput);
        });
        Assert.Single(executor.Calls);
        Assert.Equal("SystemError stage=Runner error=Io", Assert.Single(diagnostics));
    }

    [Fact]
    public async Task Cancellation_reaches_the_invocation_and_releases_the_single_submission_gate()
    {
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        var executor = new RecordingExecutor(async (_, token) =>
        {
            if (running.Task.IsCompleted) return new(IsolatedCaseStatus.Exited, []);
            running.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { stopped = true; }
            throw new InvalidOperationException("Cancellation did not stop the invocation.");
        });
        var runner = new NsJailRunner(executor, new SandboxLimits());
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var first = runner.RunAsync("python3", "while True: pass", [""], cancel.Token);
        await running.Task.WaitAsync(Token);
        var second = runner.RunAsync("python3", "print(1)", [""], Token);
        Assert.Single(executor.Calls);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.True(stopped);
        Assert.Equal(IsolatedCaseStatus.Exited, Assert.Single(await second).Status);
        Assert.Equal(2, executor.Calls.Count);
    }

    [Fact]
    public async Task Invalid_utf8_stdout_is_not_a_successful_result()
    {
        var executor = new RecordingExecutor((_, _) =>
            Task.FromResult(new SandboxExecution(IsolatedCaseStatus.Exited, [0xff])));
        var runner = new NsJailRunner(executor, new SandboxLimits());
        var result = Assert.Single(await runner.RunAsync("python3", "print(1)", [""], Token));
        Assert.Equal(IsolatedCaseStatus.RuntimeError, result.Status);
        Assert.Empty(result.StandardOutput);
    }

    private static byte[] Elf()
    {
        var bytes = new byte[64];
        "\u007fELF\u0002\u0001"u8.CopyTo(bytes);
        bytes[16] = 2;
        bytes[18] = 62;
        return bytes;
    }

    private sealed record Invocation(SandboxPhase Phase, byte[] Payload, byte[] Input);

    private sealed class RecordingExecutor(Func<Invocation, CancellationToken, Task<SandboxExecution>> execute)
        : ISandboxExecutor
    {
        public List<Invocation> Calls { get; } = [];

        public Task<SandboxExecution> ExecuteAsync(SandboxPhase phase, byte[] payload, byte[] input, SandboxBudget budget,
            CancellationToken token)
        {
            var invocation = new Invocation(phase, payload, input);
            Calls.Add(invocation);
            return execute(invocation, token);
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}

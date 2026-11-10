using System.Globalization;
using System.Text;
using CodeRank.Judge.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodeRank.Judge.Worker.Tests;

public sealed class DockerExecutionProviderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Python_uses_fresh_restricted_containers_and_only_the_current_input()
    {
        using var qa = new DockerHarness();
        var results = await qa.Provider.RunAsync("python3", "print(input())", ["first\n", "second\n"], Token);

        Assert.All(results, item => Assert.Equal(IsolatedCaseStatus.Exited, item.Status));
        Assert.Equal(new[] { "first\n", "second\n" }, results.Select(result => result.StandardOutput));
        Assert.Equal(2, qa.Cli.Created.Count);
        foreach (var create in qa.Cli.Created)
        {
            Assert.Equal("none", Value(create, "--network"));
            Assert.Equal("none", Value(create, "--ipc"));
            Assert.Equal("private", Value(create, "--cgroupns"));
            Assert.Equal("65534:65534", Value(create, "--user"));
            Assert.Equal("ALL", Value(create, "--cap-drop"));
            Assert.Equal("no-new-privileges=true", Value(create, "--security-opt"));
            Assert.Contains("--read-only", create);
            Assert.Equal("1", Value(create, "--cpus"));
            Assert.Equal("256m", Value(create, "--memory"));
            Assert.Equal("256m", Value(create, "--memory-swap"));
            Assert.Equal("32", Value(create, "--pids-limit"));
            Assert.Contains("nosuid,nodev,noexec,size=32m", Value(create, "--tmpfs"));
            Assert.Equal("none", Value(create, "--log-driver"));
            Assert.Equal("no", Value(create, "--restart"));
            Assert.Equal("never", Value(create, "--pull"));
            Assert.Equal("/usr/bin/timeout", Value(create, "--entrypoint"));
            Assert.Contains("5s", create);
            Assert.Contains(FakeDocker.Image, create);
            Assert.DoesNotContain(create, argument => argument is "--mount" or "--volume" or "-v" or "--privileged" or "--tty");
            Assert.DoesNotContain(create, argument => argument.Contains("print(input())", StringComparison.Ordinal));
            AssertPosixScript(create);
        }
        Assert.Equal(2, qa.Cli.Created.Select(command => Value(command, "--name")).Distinct().Count());
        Assert.All(qa.Cli.Attached, command => Assert.Equal("print(input())"u8.ToArray(), command.Payload.ToArray()));
        qa.AssertClean();
    }

    [Fact]
    public async Task Cpp_compiles_once_and_only_passes_the_artifact_to_case_containers()
    {
        using var qa = new DockerHarness();
        var executable = new byte[64];
        "\u007fELF"u8.CopyTo(executable);
        qa.Cli.OnStart = (command, _) => Task.FromResult(FakeDocker.Ran(
            command.OutputLimit > 64 * 1024 ? executable : command.Input.ToArray()));

        var results = await qa.Provider.RunAsync("cpp17", "int main() {}", ["8", "9"], Token);

        Assert.Equal(new[] { "8", "9" }, results.Select(result => result.StandardOutput));
        Assert.Equal(3, qa.Cli.Attached.Count);
        Assert.Equal("768m", Value(qa.Cli.Created[0], "--memory"));
        Assert.Contains("20s", qa.Cli.Created[0]);
        Assert.Contains("nodev,noexec", Value(qa.Cli.Created[0], "--tmpfs"));
        Assert.Equal("int main() {}"u8.ToArray(), qa.Cli.Attached[0].Payload.ToArray());
        Assert.Empty(qa.Cli.Attached[0].Input.ToArray());
        Assert.All(qa.Cli.Attached.Skip(1), command => Assert.Equal(executable, command.Payload.ToArray()));
        Assert.All(qa.Cli.Created.Skip(1), command => Assert.Contains("nodev,exec", Value(command, "--tmpfs")));
        Assert.All(qa.Cli.Created, AssertPosixScript);
        Assert.Single(qa.Cli.InspectedTags);
        Assert.Equal("gcc:14", qa.Cli.InspectedTags[0]);
        qa.AssertClean();
    }

    [Theory]
    [InlineData("python3", "Exited", 1, "exited|false|1|0", true, "RuntimeError", null)]
    [InlineData("cpp17", "Exited", 120, "exited|false|120|0", true, "CompilationError", null)]
    [InlineData("python3", "Exited", 137, "exited|true|137|0", true, "MemoryLimitExceeded", null)]
    [InlineData("python3", "TimedOut", -1, "running|false|0|0", true, "TimeLimitExceeded", null)]
    [InlineData("python3", "OutputLimit", -1, "running|false|0|0", true, "OutputLimitExceeded", null)]
    [InlineData("python3", "TimedOut", -1, "running|false|0|0", false, "SystemError", "Start Reason=ReadyPrefixMissing")]
    [InlineData("python3", "Failed", -1, "running|false|0|0", true, "SystemError", "Start Reason=AttachFailed")]
    [InlineData("python3", "Exited", 0, "exited|false|125|0", true, "SystemError", "Inspect Reason=ExitCodeMismatch")]
    [InlineData("python3", "Exited", 0, "exited|false|0|1", true, "SystemError", "Inspect Reason=ContainerStateError")]
    public async Task Verdict_requires_staging_exit_evidence_and_cleanup(string language, string end, int code, string state,
        bool ready, string expected, string? diagnostic)
    {
        var messages = new List<string>();
        using var qa = new DockerHarness(new DockerQaDiagnostics(messages.Add));
        qa.Cli.State = state;
        qa.Cli.OnStart = (_, _) => Task.FromResult(new DockerCommandResult(
            Enum.Parse<DockerCommandEnd>(end), code, [], ready ? Encoding.ASCII.GetBytes(DockerExecutionProvider.Ready) : []));

        var result = Assert.Single(await qa.Provider.RunAsync(language, "synthetic", [""], Token));
        Assert.Equal(expected, result.Status.ToString());
        Assert.Empty(result.StandardOutput);
        if (diagnostic is null)
            Assert.Empty(messages);
        else
            Assert.Contains(messages, message => message.Contains($"FailureStage={diagnostic}", StringComparison.Ordinal));
        qa.AssertClean();
    }

    [Fact]
    public async Task Cancellation_removes_the_owned_container_before_returning()
    {
        using var qa = new DockerHarness();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        qa.Cli.OnStart = async (_, token) =>
        {
            attached.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        };
        var run = qa.Provider.RunAsync("python3", "synthetic", [""], cancel.Token);
        await attached.Task.WaitAsync(Token);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        qa.AssertClean();
    }

    [Fact]
    public async Task Unverified_removal_overrides_cancellation_and_blocks_a_restart()
    {
        using var qa = new DockerHarness();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        qa.Cli.LeaveContainerAfterRemove = true;
        qa.Cli.OnStart = async (_, _) =>
        {
            await cancel.CancelAsync();
            return new(DockerCommandEnd.Cancelled, -1, [], []);
        };

        var result = Assert.Single(await qa.Provider.RunAsync("python3", "synthetic", [""], cancel.Token));
        Assert.Equal(IsolatedCaseStatus.SystemError, result.Status);
        Assert.Single(qa.Cli.Containers);
        Assert.True(File.Exists(qa.Marker));
        qa.Workspace.Dispose();
        Assert.Throws<InvalidOperationException>(() => DockerWorkspace.Open(qa.Temp.FullName, "judgeqa"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lease_loss_cancels_Docker_and_only_confirmed_cleanup_allows_a_new_claim(bool cleanupFails)
    {
        using var qa = new DockerHarness();
        qa.Cli.LeaveContainerAfterRemove = cleanupFails;
        var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        qa.Cli.OnStart = async (_, token) =>
        {
            attached.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        };
        using var api = new LeasedSubmissionJudgeTests.Harness(SubmissionGraderTests.Payload("python3", "private-expected"))
        {
            OnHeartbeat = (_, ok) => attached.Task.IsCompleted ? LeasedSubmissionJudgeTests.Lost() : ok()
        };

        var result = await api.RunAsync(qa.Provider, TimeProvider.System, TimeSpan.FromMilliseconds(10))
            .WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(cleanupFails ? LeasedJudgeResult.Halt : LeasedJudgeResult.LeaseEnded, result);
        Assert.Empty(api.SavedVerdicts);
        Assert.Equal(cleanupFails, File.Exists(qa.Marker));
        Assert.Single(qa.Cli.Removed);
        if (!cleanupFails) qa.AssertClean();
    }

    [Fact]
    public async Task A_daemon_failure_completes_with_SystemError_only_while_the_lease_is_valid()
    {
        using var qa = new DockerHarness();
        qa.Cli.Engine = FakeDocker.Failed;
        using var api = new LeasedSubmissionJudgeTests.Harness(SubmissionGraderTests.Payload("python3", "private-expected"));

        Assert.Equal(LeasedJudgeResult.Halt, await api.RunAsync(qa.Provider, TimeProvider.System));
        Assert.Equal("SystemError", Assert.Single(api.SavedVerdicts));
        Assert.Empty(qa.Cli.Created);
        qa.AssertClean();
    }

    [Fact]
    public async Task Losing_the_daemon_after_start_attempts_removal_and_preserves_the_recovery_marker()
    {
        using var qa = new DockerHarness();
        qa.Cli.OnStart = (_, _) =>
        {
            qa.Cli.Offline = true;
            return Task.FromResult(FakeDocker.Failed);
        };
        var result = Assert.Single(await qa.Provider.RunAsync("python3", "synthetic", [""], Token));
        Assert.Equal(IsolatedCaseStatus.SystemError, result.Status);
        Assert.Equal(Assert.Single(qa.Cli.Containers.Keys), Assert.Single(qa.Cli.OfflineRemoveAttempts));
        Assert.True(File.Exists(qa.Marker));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ambiguous_create_never_starts_a_container(bool visible)
    {
        using var qa = new DockerHarness();
        qa.Cli.CreateReplyLost = true;
        qa.Cli.VisibleAfterLostReply = visible;
        var result = Assert.Single(await qa.Provider.RunAsync("python3", "synthetic", [""], Token));
        Assert.Equal(IsolatedCaseStatus.SystemError, result.Status);
        Assert.Empty(qa.Cli.Attached);
        Assert.Equal(visible ? 1 : 0, qa.Cli.Removed.Count);
        Assert.Equal(!visible, File.Exists(qa.Marker));
    }

    [Fact]
    public async Task A_container_with_another_ownership_label_is_never_started_or_removed()
    {
        var messages = new List<string>();
        using var qa = new DockerHarness(new DockerQaDiagnostics(messages.Add));
        qa.Cli.ForeignOwner = true;
        var result = Assert.Single(await qa.Provider.RunAsync("python3", "synthetic", [""], Token));
        Assert.Equal(IsolatedCaseStatus.SystemError, result.Status);
        Assert.Empty(qa.Cli.Attached);
        Assert.Empty(qa.Cli.Removed);
        Assert.True(File.Exists(qa.Marker));
        Assert.Contains(messages, message => message.Contains("FailureStage=Ownership Reason=WorkerLabelMismatch", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("infrastructure failure FailureStage=Cleanup", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, "CommandFailed")]
    [InlineData("linux|false|true|true|true|[]", "MemoryLimitRequired")]
    [InlineData("linux|true|true|true|true|[]", "BuiltinSeccompRequired")]
    [InlineData("linux|true|true|true|true|not-json", "InvalidSecurityOptions")]
    public async Task Unavailable_daemon_or_unenforceable_policy_never_creates_a_container(string? engine, string reason)
    {
        var messages = new List<string>();
        using var qa = new DockerHarness(new DockerQaDiagnostics(messages.Add));
        qa.Cli.Engine = engine is null ? FakeDocker.Failed : FakeDocker.Ok(engine);
        var result = Assert.Single(await qa.Provider.RunAsync("python3", "synthetic", [""], Token));
        Assert.Equal(IsolatedCaseStatus.SystemError, result.Status);
        Assert.Empty(qa.Cli.Created);
        Assert.Contains(messages, message => message.Contains($"FailureStage=Engine Reason={reason}", StringComparison.Ordinal));
        qa.AssertClean();
    }

    [Theory]
    [InlineData("{}", true)]
    [InlineData("""{"Volumes":null}""", true)]
    [InlineData("""{"Volumes":{}}""", true)]
    [InlineData("""{"Volumes":{"/data":{}}}""", false)]
    [InlineData("""{"Volumes":[]}""", false)]
    [InlineData("""{"Volumes":{"/data":{}},"Volumes":null}""", false)]
    [InlineData("null", false)]
    [InlineData("{\"Volumes\":", false)]
    public async Task Image_metadata_must_confirm_that_no_volumes_are_declared(string config, bool allowed)
    {
        var messages = new List<string>();
        using var qa = new DockerHarness(new DockerQaDiagnostics(messages.Add));
        qa.Cli.ImageConfig = config;

        var result = Assert.Single(await qa.Provider.RunAsync("python3", "synthetic", [""], Token));

        Assert.Equal(allowed ? IsolatedCaseStatus.Exited : IsolatedCaseStatus.SystemError, result.Status);
        Assert.Equal(allowed ? 1 : 0, qa.Cli.Created.Count);
        if (allowed)
            Assert.Empty(messages);
        else
            Assert.Contains(messages, message => message.Contains("infrastructure failure FailureStage=Image", StringComparison.Ordinal));
        qa.AssertClean();
    }

    [Theory]
    [InlineData(8 * 1024, true)]
    [InlineData(64 * 1024, false)]
    public async Task Image_metadata_uses_a_bounded_capture_separate_from_other_control_commands(int labelSize, bool allowed)
    {
        using var qa = new DockerHarness();
        qa.Cli.ImageConfig = "{\"Labels\":{\"qa\":\"value|with|delimiters-" + new string('a', labelSize) + "\"}}";

        var result = Assert.Single(await qa.Provider.RunAsync("python3", "synthetic", [""], Token));

        Assert.Equal(allowed ? IsolatedCaseStatus.Exited : IsolatedCaseStatus.SystemError, result.Status);
        Assert.Equal(allowed ? 1 : 0, qa.Cli.Created.Count);
        qa.AssertClean();
    }

    [Fact]
    public async Task Pipe_capture_stops_at_the_byte_cap()
    {
        using var overflow = new CancellationTokenSource();
        using var source = new MemoryStream("123456789"u8.ToArray());
        using var result = new MemoryStream();
        await DockerCli.CaptureAsync(source, result, 5, overflow, Token);
        Assert.Equal("12345"u8.ToArray(), result.ToArray());
        Assert.True(overflow.IsCancellationRequested);
    }

    [Fact]
    public void Cli_is_bound_to_the_local_Desktop_pipe_and_keeps_worker_settings_out_of_its_environment()
    {
        var start = new DockerCli("docker.exe", "qa-config").StartInfo(new(["info"], TimeSpan.FromSeconds(1)));
        Assert.Equal(new[] { "--config", "qa-config", "--host", DockerCli.DesktopEndpoint, "info" }, start.ArgumentList);
        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardInput && start.RedirectStandardOutput && start.RedirectStandardError);
        Assert.DoesNotContain(start.Environment.Keys, key => key.StartsWith("DOCKER_", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("JudgeWorker__", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("", "Missing", "empty")]
    [InlineData("permission denied: private-key\n", "Missing", "permission denied")]
    [InlineData("private-key\n" + DockerExecutionProvider.Ready, "AfterOtherBytes", "unrecognized output withheld")]
    [InlineData("coderank-docker-ready-v1\r\nprivate-key", "CrlfPrefix", "unrecognized output withheld")]
    [InlineData("private-key\ncoderank-docker-ready-v1\r\n", "CrlfAfterOtherBytes", "unrecognized output withheld")]
    [InlineData(DockerExecutionProvider.Ready + "permission denied: private-key", "Prefix", "attached output withheld")]
    public void Attach_diagnostics_show_stdin_and_marker_without_echoing_program_data(string stderr, string marker, string hint)
    {
        var messages = new List<string>();
        var diagnostics = new DockerQaDiagnostics(messages.Add);
        var command = new DockerCommand(["container", "start", "--attach", "--interactive", "private-container-id"],
            TimeSpan.FromSeconds(2), Payload: "private-source"u8.ToArray(), Input: "private-input"u8.ToArray());
        var result = new DockerCommandResult(DockerCommandEnd.Exited, 1, "private-answer"u8.ToArray(),
            Encoding.UTF8.GetBytes(stderr), DockerStdinState.WriteFailed);

        diagnostics.WriteCommand(command, result);

        var message = Assert.Single(messages);
        Assert.StartsWith("DockerOperation=\"container start\" End=Exited ExitCode=1", message);
        Assert.Contains($"Stdin=WriteFailed ReadyMarker={marker}", message);
        Assert.Contains($"StderrHint=\"{hint}\"", message);
        Assert.DoesNotContain("private-", message);
        Assert.DoesNotContain('\n', message);
    }

    [Fact]
    public void Control_diagnostics_show_only_bounded_allowlisted_stderr()
    {
        var messages = new List<string>();
        var diagnostics = new DockerQaDiagnostics(messages.Add);
        var command = new DockerCommand(["image", "inspect", "--format", "private-template", "private-image"], TimeSpan.FromSeconds(10));
        var result = new DockerCommandResult(DockerCommandEnd.Exited, 1, [],
            "template parsing error: private-path\nprivate-token"u8.ToArray());

        diagnostics.WriteCommand(command, result);
        diagnostics.WriteCommand(command, result with { Error = Encoding.ASCII.GetBytes(new string('a', 1024) + "permission denied") });

        Assert.Equal(2, messages.Count);
        Assert.StartsWith("DockerOperation=\"image inspect\" End=Exited ExitCode=1", messages[0]);
        Assert.Contains("StderrHint=\"template parsing error\"", messages[0]);
        Assert.Contains("StderrHint=\"unrecognized output withheld\"", messages[1]);
        Assert.All(messages, message => Assert.DoesNotContain("private-", message));
    }

    [Fact]
    public void Opted_in_synthetic_python_stderr_reports_exact_bytes_and_caps_the_sample()
    {
        var messages = new List<string>();
        var diagnostics = new DockerQaDiagnostics(messages.Add, showPythonAcceptedStderr: true);
        var command = new DockerCommand(["container", "start", "--attach", "--interactive", "qa-id"],
            TimeSpan.FromSeconds(2), Payload: Encoding.UTF8.GetBytes(DockerDesktopQaTests.PythonAcceptedSource));
        var error = "coderank: 1: set: Illegal option -\r\n"u8.ToArray();
        var result = new DockerCommandResult(DockerCommandEnd.Exited, 2, [], error, DockerStdinState.Written);

        diagnostics.WriteCommand(command, result);

        Assert.Equal("PythonAcceptedStderr End=Exited ExitCode=2 Stdin=Written Bytes=36 ShownBytes=36 " +
            "Text=\"coderank: 1: set: Illegal option -\\r\\n\" Hex=636F646572616E6B3A20313A207365743A20496C6C6567616C206F7074696F6E202D0D0A",
            messages[1]);

        messages.Clear();
        var oversized = Encoding.UTF8.GetBytes(new string('a', 256) + "beyond-limit");
        diagnostics.WriteCommand(command, result with { Error = oversized });

        Assert.Contains($"Bytes={oversized.Length} ShownBytes=256", messages[1]);
        Assert.DoesNotContain("beyond-limit", messages[1]);
    }

    [Theory]
    [InlineData(false, "start", DockerDesktopQaTests.PythonAcceptedSource, "")]
    [InlineData(true, "inspect", DockerDesktopQaTests.PythonAcceptedSource, "")]
    [InlineData(true, "start", "print(4)", "")]
    [InlineData(true, "start", DockerDesktopQaTests.PythonAcceptedSource, "other input")]
    public void Raw_stderr_requires_opt_in_and_the_fixed_python_attach(bool optedIn, string operation, string source, string input)
    {
        var messages = new List<string>();
        var diagnostics = new DockerQaDiagnostics(messages.Add, optedIn);
        var command = new DockerCommand(["container", operation, "--attach", "--interactive", "qa-id"],
            TimeSpan.FromSeconds(2), Payload: Encoding.UTF8.GetBytes(source), Input: Encoding.UTF8.GetBytes(input));
        var result = new DockerCommandResult(DockerCommandEnd.Exited, 2, [], "raw-contents"u8.ToArray());

        diagnostics.WriteCommand(command, result);

        Assert.DoesNotContain("raw-contents", Assert.Single(messages));
    }

    [Fact]
    public async Task Failed_process_launch_reports_a_native_code_without_the_executable_path()
    {
        var executable = Path.Combine(Path.GetTempPath(), $"coderank-missing-{Guid.NewGuid():N}.exe");
        var cli = new DockerCli(executable, "qa-config");
        var command = new DockerCommand(["info"], TimeSpan.FromSeconds(1));
        var messages = new List<string>();
        var diagnostics = new DockerQaDiagnostics(messages.Add);

        var result = await cli.RunAsync(command, Token);
        diagnostics.WriteCommand(command, result);

        Assert.Equal(DockerCommandEnd.NotStarted, result.End);
        Assert.NotNull(result.NativeStartError);
        var message = Assert.Single(messages);
        Assert.Contains("End=NotStarted ExitCode=-1", message);
        Assert.Contains($"NativeStartError={result.NativeStartError}", message);
        Assert.DoesNotContain(executable, message);
    }

    private static string Value(string[] arguments, string option) => arguments[Array.IndexOf(arguments, option) + 1];

    private static void AssertPosixScript(string[] arguments)
    {
        var script = Value(arguments, "-c");
        Assert.StartsWith("set -eu\n", script);
        Assert.DoesNotContain('\r', script);
    }

    private sealed class DockerHarness : IDisposable
    {
        public DirectoryInfo Temp { get; } = Directory.CreateTempSubdirectory("coderank-docker-test-");
        public FakeDocker Cli { get; } = new();
        public DockerWorkspace Workspace { get; }
        public DockerExecutionProvider Provider { get; }
        public string Marker => Path.Combine(Workspace.DirectoryPath, "pending-container");

        public DockerHarness(ILogger<DockerExecutionProvider>? logger = null)
        {
            Workspace = DockerWorkspace.Open(Temp.FullName, "judgeqa");
            Provider = new(Cli, Workspace, new(new Uri("https://qa.example.test"), "judgeqa", ""),
                logger ?? NullLogger<DockerExecutionProvider>.Instance);
        }

        public void AssertClean()
        {
            Assert.Empty(Cli.Containers);
            Assert.False(File.Exists(Marker));
        }

        public void Dispose()
        {
            Workspace.Dispose();
            Temp.Delete(recursive: true);
        }
    }

    private sealed class FakeDocker : IDockerCli
    {
        public static readonly string Image = "sha256:" + new string('a', 64);
        public const string EngineInfo = "linux|true|true|true|true|[\"name=seccomp,profile=builtin\"]";
        public static DockerCommandResult Failed => new(DockerCommandEnd.Failed, -1, [], []);
        public DockerCommandResult Engine { get; set; } = Ok(EngineInfo);
        public string State { get; set; } = "exited|false|0|0";
        public bool LeaveContainerAfterRemove { get; set; }
        public bool CreateReplyLost { get; set; }
        public bool VisibleAfterLostReply { get; set; }
        public bool ForeignOwner { get; set; }
        public bool Offline { get; set; }
        public string ImageConfig { get; set; } = "{}";
        public List<string> InspectedTags { get; } = [];
        public List<string[]> Created { get; } = [];
        public List<DockerCommand> Attached { get; } = [];
        public List<string> Removed { get; } = [];
        public List<string> OfflineRemoveAttempts { get; } = [];
        public Dictionary<string, string> Containers { get; } = new(StringComparer.Ordinal);
        public Func<DockerCommand, CancellationToken, Task<DockerCommandResult>> OnStart { get; set; } =
            (command, _) => Task.FromResult(Ran(command.Input.ToArray()));

        public Task<DockerCommandResult> RunAsync(DockerCommand command, CancellationToken token)
        {
            var args = command.Arguments.ToArray();
            if (Offline)
            {
                if (args is ["container", "rm", "--force", var attempted]) OfflineRemoveAttempts.Add(attempted);
                return Task.FromResult(Failed);
            }
            return args switch
            {
                ["info", ..] => Task.FromResult(Engine),
                ["image", "inspect", "--format", """{"Id":{{json .Id}},"Os":{{json .Os}},"Config":{{json .Config}}}""", var tag] =>
                    Task.FromResult(InspectImage(tag, command.OutputLimit)),
                ["container", "create", ..] => Task.FromResult(Create(args)),
                ["container", "ls", ..] => Task.FromResult(List(Value(args, "--filter"))),
                ["container", "start", _, _, var id] => Attach(command, id, token),
                ["container", "inspect", _, _, var id] when Containers.ContainsKey(id) => Task.FromResult(Ok(State)),
                ["container", "rm", "--force", var id] => Task.FromResult(Remove(id)),
                _ => throw new InvalidOperationException("Unexpected Docker command.")
            };
        }

        private DockerCommandResult InspectImage(string tag, int outputLimit)
        {
            InspectedTags.Add(tag);
            var output = Encoding.UTF8.GetBytes($"{{\"Id\":\"{Image}\",\"Os\":\"linux\",\"Config\":{ImageConfig}}}\n");
            return output.Length > outputLimit
                ? new(DockerCommandEnd.OutputLimit, -1, output[..outputLimit], [])
                : new(DockerCommandEnd.Exited, 0, output, []);
        }

        private DockerCommandResult Create(string[] args)
        {
            Created.Add(args);
            var id = Created.Count.ToString("x64", CultureInfo.InvariantCulture);
            var name = Value(args, "--name");
            var execution = name["coderank-judge-".Length..];
            if (!CreateReplyLost || VisibleAfterLostReply)
                Containers[id] = $"{id}|{name}|{(ForeignOwner ? "other" : "judgeqa")}|{execution}";
            return CreateReplyLost ? Failed : Ok(id);
        }

        private DockerCommandResult List(string filter)
        {
            if (filter.StartsWith("id=", StringComparison.Ordinal))
                return Ok(Containers.GetValueOrDefault(filter[3..], ""));
            var name = filter["name=^/".Length..^1];
            return Ok(string.Join('\n', Containers.Values.Where(value => value.Split('|')[1] == name)));
        }

        private Task<DockerCommandResult> Attach(DockerCommand command, string id, CancellationToken token)
        {
            Assert.Contains(id, Containers.Keys);
            Attached.Add(command);
            return OnStart(command, token);
        }

        private DockerCommandResult Remove(string id)
        {
            Assert.Contains(id, Containers.Keys);
            Removed.Add(id);
            if (!LeaveContainerAfterRemove) Containers.Remove(id);
            return Ok(id);
        }

        public static DockerCommandResult Ok(string value) => new(DockerCommandEnd.Exited, 0, Encoding.ASCII.GetBytes(value), []);
        public static DockerCommandResult Ran(byte[] output) =>
            new(DockerCommandEnd.Exited, 0, output, Encoding.ASCII.GetBytes(DockerExecutionProvider.Ready));
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeRank.Judge.Protocol;

namespace CodeRank.Judge.Worker;

internal sealed class DockerExecutionProvider(IDockerCli cli, DockerWorkspace workspace, WorkerSettings settings,
    ILogger<DockerExecutionProvider> logger) : IIsolatedJudgeRunner
{
    internal const string WorkerLabel = "io.coderank.judge.worker";
    internal const string ExecutionLabel = "io.coderank.judge.execution";
    internal const string Ready = "coderank-docker-ready-v1\n";
    private const string PythonImage = "python:3.12-slim";
    private const string CppImage = "gcc:14";
    private const int CompileFailure = 120;
    private const int MaxStreamBytes = 64 * 1024;
    private const int MaxImageMetadataBytes = 64 * 1024;
    private const int MaxExecutableBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SubmissionTimeout = TimeSpan.FromMinutes(15);
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private const string StageFile = """
        set -eu
        umask 077
        stage() {
          head -c "$2" > "$1"
          [ "$(wc -c < "$1")" -eq "$2" ]
        }

        """;
    private const string Compile = StageFile + """
        stage /tmp/Main.cpp "$1"
        command -v g++ >/dev/null
        printf 'coderank-docker-ready-v1\n' >&2
        g++ -std=c++17 -O2 /tmp/Main.cpp -o /tmp/program || exit 120
        test -s /tmp/program
        exec cat /tmp/program
        """;
    private const string Python = StageFile + """
        stage /tmp/Main.py "$1"
        stage /tmp/input "$2"
        command -v python3.12 >/dev/null
        printf 'coderank-docker-ready-v1\n' >&2
        exec python3.12 -I -B -u /tmp/Main.py </tmp/input
        """;
    private const string Cpp = StageFile + """
        stage /tmp/program "$1"
        stage /tmp/input "$2"
        chmod 500 /tmp/program
        printf 'coderank-docker-ready-v1\n' >&2
        exec /tmp/program </tmp/input
        """;

    private enum Phase { Compile, Python, Cpp }
    private enum FailureStage { Engine, Image, Create, Ownership, Start, Inspect, Cleanup, Deadline, InputOrLocalState }
    private sealed class DockerFailure(FailureStage stage) : Exception
    {
        public FailureStage Stage { get; } = stage;
    }
    private readonly record struct PhaseResult(IsolatedCaseStatus Status, byte[] Output);

    public async Task<IReadOnlyList<IsolatedCaseResult>> RunAsync(string language, string sourceCode,
        IReadOnlyList<string> inputs, CancellationToken token)
    {
        using var deadline = new CancellationTokenSource(SubmissionTimeout);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            if (language is not ("python3" or "cpp17") || inputs.Count is < 1 or > JudgePayloadContract.MaxTests)
                throw new DockerFailure(FailureStage.InputOrLocalState);
            var source = Encode(sourceCode, JudgePayloadContract.MaxSourceBytes);
            if (source.Length == 0) throw new DockerFailure(FailureStage.InputOrLocalState);
            var cases = inputs.Select(input => Encode(input, JudgePayloadContract.MaxTestFieldBytes)).ToArray();
            await RequireEngineAsync(session.Token);
            var phase = language == "cpp17" ? Phase.Cpp : Phase.Python;
            var image = await ResolveImageAsync(phase == Phase.Cpp ? CppImage : PythonImage, session.Token);
            var payload = source;
            if (phase == Phase.Cpp)
            {
                var compiled = await RunPhaseAsync(Phase.Compile, image, source, [], session.Token);
                session.Token.ThrowIfCancellationRequested();
                if (compiled.Status != IsolatedCaseStatus.Exited)
                    return Enumerable.Repeat(Result(compiled.Status), cases.Length).ToArray();
                if (compiled.Output.Length < 64 || !compiled.Output.AsSpan().StartsWith("\u007fELF"u8))
                    throw Reject(FailureStage.Start, "InvalidCompilerArtifact");
                payload = compiled.Output;
            }

            var results = new List<IsolatedCaseResult>(cases.Length);
            foreach (var input in cases)
            {
                var observed = await RunPhaseAsync(phase, image, payload, input, session.Token);
                results.Add(DecodeResult(observed));
            }
            session.Token.ThrowIfCancellationRequested();
            return results;
        }
        catch (DockerFailure error) { return Fail(error.Stage); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
        {
            return Fail(FailureStage.Deadline);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or JsonException or EncoderFallbackException)
        {
            logger.LogDebug("Docker Judge local failure ErrorKind={ErrorKind}.", error.GetType().Name);
            return Fail(FailureStage.InputOrLocalState);
        }
    }

    private async Task<PhaseResult> RunPhaseAsync(Phase phase, string image, byte[] payload, byte[] input,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var execution = Guid.NewGuid().ToString("N");
        var name = "coderank-judge-" + execution;
        var compiling = phase == Phase.Compile;
        var memory = compiling ? "768m" : "256m";
        var access = phase == Phase.Cpp ? "exec" : "noexec";
        var script = phase switch { Phase.Compile => Compile, Phase.Python => Python, _ => Cpp };
        string? createdId = null;
        string? ownedId = null;
        var createDidNotStart = false;

        workspace.Record(execution);
        try
        {
            var created = await ControlAsync([
                "container", "create", "--pull", "never", "--name", name,
                "--label", $"{WorkerLabel}={settings.WorkerId}", "--label", $"{ExecutionLabel}={execution}",
                "--network", "none", "--ipc", "none", "--cgroupns", "private",
                "--user", "65534:65534", "--cap-drop", "ALL", "--security-opt", "no-new-privileges=true",
                "--read-only", "--cpus", "1", "--memory", memory, "--memory-swap", memory, "--pids-limit", "32",
                "--tmpfs", $"/tmp:rw,nosuid,nodev,{access},size=32m,mode=0700,uid=65534,gid=65534",
                "--workdir", "/tmp", "--env", "HOME=/tmp", "--env", "TMPDIR=/tmp",
                "--init", "--restart", "no", "--no-healthcheck", "--log-driver", "none", "--interactive",
                "--entrypoint", "/usr/bin/timeout", image, "--signal=KILL", compiling ? "20s" : "5s",
                "/bin/sh", "-c", script, "coderank",
                payload.Length.ToString(CultureInfo.InvariantCulture), input.Length.ToString(CultureInfo.InvariantCulture)
            ]);
            createDidNotStart = created.End == DockerCommandEnd.NotStarted;
            if (!created.Succeeded) throw Reject(FailureStage.Create, "CommandFailed");
            var id = Text(created);
            if (!IsContainerId(id)) throw Reject(FailureStage.Create, "InvalidContainerId");
            createdId = id;
            ownedId = await FindOwnedAsync(name, execution, createdId);
            if (ownedId is null) throw Reject(FailureStage.Ownership, "ContainerNotListed");
            token.ThrowIfCancellationRequested();

            var attached = await cli.RunAsync(new(
                ["container", "start", "--attach", "--interactive", ownedId],
                TimeSpan.FromSeconds(compiling ? 15 : 2), compiling ? MaxExecutableBytes : MaxStreamBytes,
                MaxStreamBytes + Ready.Length, payload, input), token);
            token.ThrowIfCancellationRequested();
            return await ClassifyAsync(phase, ownedId, attached);
        }
        finally
        {
            // The lease token can be cancelled. Cleanup has its own bounded control commands.
            if (!createDidNotStart)
                await RemoveOwnedAsync(name, execution, createdId, ownedId);
            workspace.ConfirmRemoved();
        }
    }

    private async Task RequireEngineAsync(CancellationToken token)
    {
        var result = await ControlAsync(["info", "--format",
            "{{.OSType}}|{{.MemoryLimit}}|{{.SwapLimit}}|{{.CPUCfsQuota}}|{{.PidsLimit}}|{{json .SecurityOptions}}"], token);
        token.ThrowIfCancellationRequested();
        if (!result.Succeeded) throw Reject(FailureStage.Engine, "CommandFailed");
        var fields = Text(result).Split('|', 6);
        if (fields.Length != 6) throw Reject(FailureStage.Engine, "UnexpectedInfoShape");
        if (fields[0] != "linux") throw Reject(FailureStage.Engine, "LinuxEngineRequired");
        if (fields[1] != "true") throw Reject(FailureStage.Engine, "MemoryLimitRequired");
        if (fields[2] != "true") throw Reject(FailureStage.Engine, "SwapLimitRequired");
        if (fields[3] != "true") throw Reject(FailureStage.Engine, "CpuQuotaRequired");
        if (fields[4] != "true") throw Reject(FailureStage.Engine, "PidsLimitRequired");
        try
        {
            if (JsonSerializer.Deserialize<string[]>(fields[5])?.Contains("name=seccomp,profile=builtin", StringComparer.Ordinal) != true)
                throw Reject(FailureStage.Engine, "BuiltinSeccompRequired");
        }
        catch (JsonException)
        {
            throw Reject(FailureStage.Engine, "InvalidSecurityOptions");
        }
    }

    private async Task<string> ResolveImageAsync(string tag, CancellationToken token)
    {
        const string format = """{"Id":{{json .Id}},"Os":{{json .Os}},"Config":{{json .Config}}}""";
        var result = await cli.RunAsync(new(["image", "inspect", "--format", format, tag],
            ControlTimeout, OutputLimit: MaxImageMetadataBytes), token);
        token.ThrowIfCancellationRequested();
        if (!result.Succeeded) throw Reject(FailureStage.Image, "CommandFailed");

        try
        {
            using var metadata = JsonDocument.Parse(result.Output, new JsonDocumentOptions { AllowDuplicateProperties = false });
            var root = metadata.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("Id", out var idValue) || idValue.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("Os", out var os) || os.ValueKind != JsonValueKind.String || !os.ValueEquals("linux"u8) ||
                !root.TryGetProperty("Config", out var config) || config.ValueKind != JsonValueKind.Object)
                throw Reject(FailureStage.Image, "InvalidLinuxImageMetadata");

            var id = idValue.GetString()!;
            if (!id.StartsWith("sha256:", StringComparison.Ordinal) || !IsContainerId(id[7..]))
                throw Reject(FailureStage.Image, "InvalidImageId");
            if (config.TryGetProperty("Volumes", out var volumes) && volumes.ValueKind != JsonValueKind.Null &&
                (volumes.ValueKind != JsonValueKind.Object || volumes.EnumerateObject().MoveNext()))
                throw Reject(FailureStage.Image, "DeclaredOrInvalidVolumes");
            return id;
        }
        catch (JsonException)
        {
            throw Reject(FailureStage.Image, "InvalidImageJson");
        }
    }

    private async Task<string?> FindOwnedAsync(string name, string execution, string? expectedId)
    {
        var result = await ControlAsync(["container", "ls", "--all", "--no-trunc", "--filter",
            expectedId is null ? $"name=^/{name}$" : $"id={expectedId}", "--format",
            "{{.ID}}|{{.Names}}|{{.Label \"" + WorkerLabel + "\"}}|{{.Label \"" + ExecutionLabel + "\"}}"]);
        if (!result.Succeeded) throw Reject(FailureStage.Ownership, "CommandFailed");
        var text = Text(result);
        if (text.Length == 0) return null;
        var fields = text.Split('|');
        if (fields.Length != 4 || !IsContainerId(fields[0]))
            throw Reject(FailureStage.Ownership, "InvalidContainerListRow");
        if (expectedId is not null && fields[0] != expectedId) throw Reject(FailureStage.Ownership, "ContainerIdMismatch");
        if (fields[1] != name) throw Reject(FailureStage.Ownership, "ContainerNameMismatch");
        if (fields[2] != settings.WorkerId) throw Reject(FailureStage.Ownership, "WorkerLabelMismatch");
        if (fields[3] != execution) throw Reject(FailureStage.Ownership, "ExecutionLabelMismatch");
        return fields[0];
    }

    private async Task RemoveOwnedAsync(string name, string execution, string? createdId, string? ownedId)
    {
        try
        {
            var id = ownedId ?? await FindOwnedAsync(name, execution, createdId);
            if (id is null)
            {
                // An interrupted create can still appear in Docker after this lookup. Leave the recovery marker.
                if (createdId is null) throw Reject(FailureStage.Cleanup, "CreateOutcomeUnconfirmed");
                return;
            }
            _ = await ControlAsync(["container", "rm", "--force", id]);
            if (await FindOwnedAsync(name, execution, id) is not null)
                throw Reject(FailureStage.Cleanup, "ContainerStillListed");
        }
        catch (DockerFailure)
        {
            throw new DockerFailure(FailureStage.Cleanup);
        }
    }

    private async Task<PhaseResult> ClassifyAsync(Phase phase, string id, DockerCommandResult attached)
    {
        if (attached.End is not (DockerCommandEnd.Exited or DockerCommandEnd.TimedOut or DockerCommandEnd.OutputLimit))
            throw Reject(FailureStage.Start, "AttachFailed");
        if (!attached.Error.AsSpan().StartsWith(Encoding.ASCII.GetBytes(Ready)))
            throw Reject(FailureStage.Start, "ReadyPrefixMissing");
        var inspected = await ControlAsync(["container", "inspect", "--format",
            "{{.State.Status}}|{{.State.OOMKilled}}|{{.State.ExitCode}}|{{len .State.Error}}", id]);
        if (!inspected.Succeeded) throw Reject(FailureStage.Inspect, "CommandFailed");
        var state = Text(inspected).Split('|');
        if (state.Length != 4 || state[0] is not ("exited" or "running") || !bool.TryParse(state[1], out var oom))
            throw Reject(FailureStage.Inspect, "UnexpectedContainerState");
        if (state[3] != "0") throw Reject(FailureStage.Inspect, "ContainerStateError");
        if (oom) return new(IsolatedCaseStatus.MemoryLimitExceeded, []);
        if (attached.End == DockerCommandEnd.TimedOut) return new(IsolatedCaseStatus.TimeLimitExceeded, []);
        if (attached.End == DockerCommandEnd.OutputLimit) return new(IsolatedCaseStatus.OutputLimitExceeded, []);
        if (state[0] != "exited") throw Reject(FailureStage.Inspect, "ContainerNotExited");
        if (state[2] != attached.ExitCode.ToString(CultureInfo.InvariantCulture))
            throw Reject(FailureStage.Inspect, "ExitCodeMismatch");
        if (attached.ExitCode == 0) return new(IsolatedCaseStatus.Exited, attached.Output);
        if (phase != Phase.Compile) return new(IsolatedCaseStatus.RuntimeError, []);
        if (attached.ExitCode == CompileFailure) return new(IsolatedCaseStatus.CompilationError, []);
        throw Reject(FailureStage.Start, "UnexpectedCompilerExitCode");
    }

    private Task<DockerCommandResult> ControlAsync(string[] args, CancellationToken token = default) =>
        cli.RunAsync(new(args, ControlTimeout), token);

    private DockerFailure Reject(FailureStage stage, string reason)
    {
        // Record the first rejection before the cleanup path can report a separate failure.
        logger.LogDebug("Docker Judge rejected operation FailureStage={FailureStage} Reason={Reason}.", stage, reason);
        return new(stage);
    }

    private IReadOnlyList<IsolatedCaseResult> Fail(FailureStage stage)
    {
        logger.LogError("Docker Judge infrastructure failure FailureStage={FailureStage}.", stage);
        return [Result(IsolatedCaseStatus.SystemError)];
    }

    private static IsolatedCaseResult DecodeResult(PhaseResult result)
    {
        if (result.Status != IsolatedCaseStatus.Exited) return Result(result.Status);
        try { return Result(result.Status, Utf8.GetString(result.Output)); }
        catch (DecoderFallbackException) { return Result(IsolatedCaseStatus.RuntimeError); }
    }

    private static byte[] Encode(string value, int limit)
    {
        if (value.Length > limit || Utf8.GetByteCount(value) > limit)
            throw new DockerFailure(FailureStage.InputOrLocalState);
        return Utf8.GetBytes(value);
    }

    private static IsolatedCaseResult Result(IsolatedCaseStatus status, string output = "") =>
        new() { Status = status, StandardOutput = output };

    private static string Text(DockerCommandResult result) => Encoding.ASCII.GetString(result.Output).Trim();
    private static bool IsContainerId(string value) =>
        value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

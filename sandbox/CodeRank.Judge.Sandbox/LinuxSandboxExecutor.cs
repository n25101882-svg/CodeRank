using System.Buffers;
using System.Diagnostics;
using CodeRank.Judge.Worker;

namespace CodeRank.Judge.Sandbox;

internal sealed class LinuxSandboxExecutor(SandboxLayout? layout = null) : ISandboxExecutor
{
    public async Task<SandboxExecution> ExecuteAsync(SandboxPhase phase, byte[] payload, byte[] input,
        SandboxBudget budget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux()) throw new SandboxUnavailableException(SandboxStage.Platform);
        var plan = new NsJailPlan(phase, budget, Guid.NewGuid(), layout);
        LinuxSandboxHost.Verify(plan);
        var group = new LinuxJobCgroup(plan.CgroupDirectory, plan.Layout.Cgroup);
        var stage = SandboxStage.ScratchPreparation;
        var created = false;
        var executableCreated = false;
        SandboxExecution? result = null;
        SandboxFailure? failure = null;
        try
        {
            Directory.CreateDirectory(plan.Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            created = true;
            stage = SandboxStage.CgroupSetup;
            group.Create(budget);
            stage = SandboxStage.ScratchPreparation;
            await using (var file = new FileStream(plan.PayloadFile, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            }))
            {
                executableCreated = phase == SandboxPhase.Cpp17;
                await file.WriteAsync(payload, token);
                if (executableCreated)
                    File.SetUnixFileMode(file.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }
            await File.WriteAllTextAsync(plan.ConfigFile, plan.Config, token);
            await File.WriteAllTextAsync(plan.FilterFile, plan.Filter, token);
            stage = SandboxStage.LauncherStart;
            var (observation, oom) = await InvokeAsync(plan, group, budget, input, token);
            token.ThrowIfCancellationRequested();
            stage = SandboxStage.Receipt;
            var receipt = SandboxReceipt.Read(plan.StatusFile);
            failure = SandboxReceipt.Explain(receipt, observation.Stderr.Bytes, observation.TimedOut,
                observation.Stderr.Status);
            var status = Classify(phase, receipt ?? "", oom, observation.Overflow, observation.TimedOut, observation.ExitCode);
            result = new(status, status == IsolatedCaseStatus.Exited ? observation.Stdout : []);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (SandboxFailure.IsInfrastructureError(error))
        {
            failure = SandboxFailure.From(stage, error);
        }
        finally
        {
            // On a failed kill or unconfirmed exit, leave the paths for the operator and poison this Runner.
            if (created)
            {
                try
                {
                    RemoveResources(group.Remove, plan.Layout.Scratch, plan.Directory);
                    if (executableCreated) File.Delete(plan.PayloadFile);
                }
                catch (Exception error) when (SandboxFailure.IsInfrastructureError(error))
                {
                    var cleanup = SandboxFailure.From(SandboxStage.ScratchCleanup, error);
                    failure = failure?.WithCleanup(cleanup.Cause) ?? cleanup;
                }
            }
        }
        if (failure is not null) return new(IsolatedCaseStatus.SystemError, [], failure);
        token.ThrowIfCancellationRequested();
        return result ?? throw new SandboxUnavailableException(stage);
    }

    internal static void RemoveResources(Action removeCgroup, string scratch, string directory)
    {
        removeCgroup();
        var path = Path.GetFullPath(directory);
        var name = Path.GetFileName(path);
        if (Path.GetDirectoryName(path) != Path.GetFullPath(scratch) ||
            !name.StartsWith("job-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(name.AsSpan(4), "N", out _) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new SandboxUnavailableException(SandboxStage.ScratchCleanup);
        Directory.Delete(path, recursive: true);
    }

    internal static IsolatedCaseStatus Classify(SandboxPhase phase, string receipt, bool oom,
        bool overflow, bool timedOut, int exitCode)
    {
        if (receipt != "R") return IsolatedCaseStatus.SystemError;
        if (oom) return IsolatedCaseStatus.MemoryLimitExceeded;
        if (overflow) return IsolatedCaseStatus.OutputLimitExceeded;
        if (timedOut) return IsolatedCaseStatus.TimeLimitExceeded;
        if (exitCode == 0) return IsolatedCaseStatus.Exited;
        return phase == SandboxPhase.Compile ? IsolatedCaseStatus.CompilationError : IsolatedCaseStatus.RuntimeError;
    }

    private static async Task<(Observation Observation, bool Oom)> InvokeAsync(NsJailPlan plan, LinuxJobCgroup group,
        SandboxBudget budget, byte[] input, CancellationToken token)
    {
        using var process = new Process { StartInfo = plan.StartInfo };
        var invocationStarted = Stopwatch.GetTimestamp();
        using var deadline = new CancellationTokenSource(budget.WallTime);
        using var overflow = new CancellationTokenSource();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token, overflow.Token);
        var started = false;
        Task<byte[]>? stdout = null;
        Task<StreamCapture>? stderr = null;
        Task? io = null;
        var stage = SandboxStage.LauncherStart;
        SandboxFailure? failure = null;
        bool timedOut;
        var oom = false;
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            group.MarkLaunching();
            started = process.Start();
            if (!started) throw new SandboxUnavailableException(stage);
            stage = SandboxStage.ProcessIo;
            stdout = CaptureAsync(process.StandardOutput.BaseStream, budget.StdoutBytes, overflow, stop.Token);
            stderr = CaptureStderrAsync(process.StandardError.BaseStream, budget.StderrBytes, overflow, stop.Token);
            io = Task.WhenAll(stdout, stderr, SupplyInputAsync(process, input, stop.Token), process.WaitForExitAsync(stop.Token));
            await io.WaitAsync(stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) when (SandboxFailure.IsInfrastructureError(error))
        {
            failure = SandboxFailure.From(stage, error);
        }
        finally
        {
            timedOut = deadline.IsCancellationRequested;
            deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            var reason = timedOut ? SandboxStopReason.WallDeadline : token.IsCancellationRequested ? SandboxStopReason.Cancelled :
                overflow.IsCancellationRequested ? SandboxStopReason.OutputLimit : SandboxStopReason.ProcessIo;
            stop.Cancel();
            try { oom = await group.StopAsync(process, started, new(invocationStarted, budget.WallTime, reason)); }
            catch (Exception error) when (SandboxFailure.IsInfrastructureError(error))
            {
                var cleanup = SandboxFailure.From(SandboxStage.CgroupCleanup, error);
                failure = failure?.WithCleanup(cleanup.Cause) ?? cleanup;
            }
            if (io is not null)
            {
                try { await io.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (TimeoutException)
                {
                    var cleanup = new SandboxDiagnostic(SandboxStage.ProcessCleanup, SandboxFault.Timeout);
                    failure = failure?.WithCleanup(cleanup) ?? new(cleanup);
                }
                catch (Exception error) when (SandboxFailure.IsInfrastructureError(error))
                {
                    failure ??= SandboxFailure.From(SandboxStage.ProcessIo, error);
                }
            }
        }
        if (failure is not null) throw new SandboxUnavailableException(failure);
        token.ThrowIfCancellationRequested();
        return (new(stdout is { IsCompletedSuccessfully: true } ? stdout.Result : [],
            stderr is { IsCompletedSuccessfully: true } ? stderr.Result : new([], SandboxStderrStatus.Unavailable),
            started ? process.ExitCode : -1,
            timedOut, overflow.IsCancellationRequested), oom);
    }

    internal static async Task<byte[]> CaptureAsync(Stream stream, int limit, CancellationTokenSource overflow,
        CancellationToken token) => (await CaptureStreamAsync(stream, limit, overflow, token, preservePrefix: false)).Bytes;

    internal static Task<StreamCapture> CaptureStderrAsync(Stream stream, int limit, CancellationTokenSource overflow,
        CancellationToken token) => CaptureStreamAsync(stream, limit, overflow, token, preservePrefix: true);

    private static async Task<StreamCapture> CaptureStreamAsync(Stream stream, int limit, CancellationTokenSource overflow,
        CancellationToken token, bool preservePrefix)
    {
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, 8192), token);
                if (read == 0) return new(output.ToArray(), SandboxStderrStatus.Complete);
                var remaining = limit - (int)output.Length;
                if (read > remaining)
                {
                    if (preservePrefix) output.Write(buffer, 0, remaining);
                    overflow.Cancel();
                    return new(preservePrefix ? output.ToArray() : [], SandboxStderrStatus.LimitExceeded);
                }
                output.Write(buffer, 0, read);
            }
        }
        catch (OperationCanceledException) when (preservePrefix && token.IsCancellationRequested)
        {
            return new(output.ToArray(), SandboxStderrStatus.Interrupted);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async Task SupplyInputAsync(Process process, byte[] input, CancellationToken token)
    {
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(input, token);
            await process.StandardInput.BaseStream.FlushAsync(token);
        }
        catch (IOException) { } // The program can close stdin without consuming the test input.
        finally { process.StandardInput.Close(); }
    }

    internal sealed record StreamCapture(byte[] Bytes, SandboxStderrStatus Status);

    private sealed record Observation(byte[] Stdout, StreamCapture Stderr, int ExitCode, bool TimedOut, bool Overflow);
}

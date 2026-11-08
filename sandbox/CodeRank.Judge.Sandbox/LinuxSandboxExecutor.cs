using System.Buffers;
using System.Diagnostics;
using System.Runtime.Versioning;
using CodeRank.Judge.Worker;

namespace CodeRank.Judge.Sandbox;

internal sealed class LinuxSandboxExecutor : ISandboxExecutor
{
    public async Task<SandboxExecution> ExecuteAsync(SandboxPhase phase, byte[] payload, byte[] input,
        SandboxBudget budget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux()) throw new SandboxUnavailableException();
        var plan = new NsJailPlan(phase, budget, Guid.NewGuid());
        LinuxSandboxHost.Verify(plan);
        var group = new LinuxJobCgroup(plan.CgroupDirectory);
        Directory.CreateDirectory(plan.Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            group.Create(budget);
            await WritePayloadAsync(plan.PayloadFile, phase, payload, token);
            await File.WriteAllTextAsync(plan.ConfigFile, plan.Config, token);
            await File.WriteAllTextAsync(plan.FilterFile, plan.Filter, token);
            var (observation, oom) = await InvokeAsync(plan, group, budget, input, token);
            token.ThrowIfCancellationRequested();
            var receipt = ReadReceipt(plan.StatusFile);
            var status = Classify(phase, receipt, oom, observation.Overflow, observation.TimedOut, observation.ExitCode);
            return new(status, status == IsolatedCaseStatus.Exited ? observation.Stdout : []);
        }
        finally
        {
            // On a failed kill or unconfirmed exit, leave the paths for the operator and poison this Runner.
            RemoveResources(group.Remove, SandboxLayout.Scratch, plan.Directory);
        }
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
            throw new SandboxUnavailableException();
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

    private static string ReadReceipt(string path)
    {
        if (!File.Exists(path)) return "";
        using var file = File.OpenRead(path);
        return file.Length == 1 && file.ReadByte() == 'R' ? "R" : "";
    }

    [SupportedOSPlatform("linux")]
    private static async Task WritePayloadAsync(string path, SandboxPhase phase, byte[] payload, CancellationToken token)
    {
        await using var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        await file.WriteAsync(payload, token);
        if (phase == SandboxPhase.Cpp17)
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
    }

    private static async Task<(Observation Observation, bool Oom)> InvokeAsync(NsJailPlan plan, LinuxJobCgroup group,
        SandboxBudget budget, byte[] input, CancellationToken token)
    {
        using var process = new Process { StartInfo = plan.StartInfo };
        using var deadline = new CancellationTokenSource(budget.WallTime);
        using var overflow = new CancellationTokenSource();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token, overflow.Token);
        var started = false;
        Task<byte[]>? stdout = null;
        Task? io = null;
        bool timedOut;
        bool oom;
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            group.MarkLaunching();
            started = process.Start();
            if (!started) throw new SandboxUnavailableException();
            stdout = CaptureAsync(process.StandardOutput.BaseStream, budget.StdoutBytes, overflow, stop.Token);
            var stderr = CaptureAsync(process.StandardError.BaseStream, budget.StderrBytes, overflow, stop.Token);
            io = Task.WhenAll(stdout, stderr, SupplyInputAsync(process, input, stop.Token), process.WaitForExitAsync(stop.Token));
            await io.WaitAsync(stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            timedOut = deadline.IsCancellationRequested;
            deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            stop.Cancel();
            try { oom = await group.StopAsync(process, started); }
            finally
            {
                if (io is not null)
                {
                    try { await io.WaitAsync(TimeSpan.FromSeconds(3)); }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                    catch (TimeoutException) { throw new SandboxUnavailableException(); }
                }
            }
        }
        token.ThrowIfCancellationRequested();
        return (new(stdout is { IsCompletedSuccessfully: true } ? stdout.Result : [], started ? process.ExitCode : -1,
            timedOut, overflow.IsCancellationRequested), oom);
    }

    internal static async Task<byte[]> CaptureAsync(Stream stream, int limit, CancellationTokenSource overflow,
        CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, 8192), token);
                if (read == 0) return output.ToArray();
                if (read > limit - output.Length)
                {
                    overflow.Cancel();
                    return [];
                }
                output.Write(buffer, 0, read);
            }
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

    private sealed record Observation(byte[] Stdout, int ExitCode, bool TimedOut, bool Overflow);
}

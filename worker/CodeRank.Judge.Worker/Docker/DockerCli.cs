using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace CodeRank.Judge.Worker;

internal enum DockerCommandEnd { Exited, TimedOut, OutputLimit, Cancelled, NotStarted, Failed }

internal sealed record DockerCommand(IReadOnlyList<string> Arguments, TimeSpan Timeout,
    int OutputLimit = 4096, int ErrorLimit = 4096, ReadOnlyMemory<byte> Payload = default,
    ReadOnlyMemory<byte> Input = default);

internal sealed record DockerCommandResult(DockerCommandEnd End, int ExitCode, byte[] Output, byte[] Error)
{
    public bool Succeeded => End == DockerCommandEnd.Exited && ExitCode == 0;
}

internal interface IDockerCli
{
    Task<DockerCommandResult> RunAsync(DockerCommand command, CancellationToken token);
}

internal sealed class DockerCli(string executable, string configDirectory) : IDockerCli
{
    internal const string DesktopEndpoint = "npipe:////./pipe/dockerDesktopLinuxEngine";
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(2);

    internal ProcessStartInfo StartInfo(DockerCommand command)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var key in start.Environment.Keys.Where(key =>
            key.StartsWith("DOCKER_", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("JudgeWorker__", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        foreach (var argument in new[] { "--config", configDirectory, "--host", DesktopEndpoint })
            start.ArgumentList.Add(argument);
        foreach (var argument in command.Arguments)
            start.ArgumentList.Add(argument);
        return start;
    }

    public async Task<DockerCommandResult> RunAsync(DockerCommand command, CancellationToken token)
    {
        if (token.IsCancellationRequested) return new(DockerCommandEnd.Cancelled, -1, [], []);
        using var process = new Process { StartInfo = StartInfo(command) };
        try
        {
            if (!process.Start()) return new(DockerCommandEnd.NotStarted, -1, [], []);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            return new(DockerCommandEnd.NotStarted, -1, [], []);
        }

        var elapsed = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(command.Timeout);
        using var overflow = new CancellationTokenSource();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token, overflow.Token);
        using var output = new MemoryStream();
        using var errorOutput = new MemoryStream();
        var io = Task.WhenAll(
            CaptureAsync(process.StandardOutput.BaseStream, output, command.OutputLimit, overflow, stop.Token),
            CaptureAsync(process.StandardError.BaseStream, errorOutput, command.ErrorLimit, overflow, stop.Token),
            SupplyInputAsync(process, command, stop.Token), process.WaitForExitAsync());

        DockerCommandEnd end;
        try
        {
            await io.WaitAsync(stop.Token);
            end = EndState();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            end = EndState();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            end = DockerCommandEnd.Failed;
        }
        if (end != DockerCommandEnd.Exited)
        {
            await stop.CancelAsync();
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await io.WaitAsync(ExitGrace);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested && process.HasExited) { }
            catch (Exception error) when (error is IOException or InvalidOperationException or Win32Exception or TimeoutException)
            {
                // The caller still verifies the engine-side container independently of this CLI process.
                return new(DockerCommandEnd.Failed, -1, [], []);
            }
        }

        return new(end, process.HasExited ? process.ExitCode : -1, output.ToArray(), errorOutput.ToArray());

        DockerCommandEnd EndState() => token.IsCancellationRequested ? DockerCommandEnd.Cancelled :
            overflow.IsCancellationRequested ? DockerCommandEnd.OutputLimit :
            deadline.IsCancellationRequested || elapsed.Elapsed >= command.Timeout ? DockerCommandEnd.TimedOut : DockerCommandEnd.Exited;
    }

    internal static async Task CaptureAsync(Stream source, Stream target, int limit,
        CancellationTokenSource overflow, CancellationToken token)
    {
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer, token);
            if (read == 0) return;
            var retained = Math.Min(read, limit - (int)target.Length);
            target.Write(buffer.AsSpan(0, retained));
            if (retained == read) continue;
            await overflow.CancelAsync();
            return;
        }
    }

    private static async Task SupplyInputAsync(Process process, DockerCommand command, CancellationToken token)
    {
        using var input = process.StandardInput;
        try
        {
            await input.BaseStream.WriteAsync(command.Payload, token);
            await input.BaseStream.WriteAsync(command.Input, token);
        }
        catch (IOException)
        {
            // The container may exit before stdin closes. The provider checks the staging marker and exit state.
        }
    }
}

using System.Text;
using System.Text.Json;
using CodeRank.Judge.Worker;
using Microsoft.Extensions.Logging;

namespace CodeRank.Judge.Worker.Tests;

internal sealed class DockerQaDiagnostics(Action<string> write, bool showPythonAcceptedStderr = false) : ILogger<DockerExecutionProvider>
{
    private const int StderrSampleBytes = 1024;
    private const int RawStderrSampleBytes = 256;
    private static readonly byte[] Ready = Encoding.ASCII.GetBytes(DockerExecutionProvider.Ready);
    private static readonly byte[] PythonAcceptedSource = Encoding.UTF8.GetBytes(DockerDesktopQaTests.PythonAcceptedSource);
    private static readonly string[] StderrHints =
    [
        "map has no entry for key", "template parsing error", "can't evaluate field", "unknown flag",
        "the system cannot find the file specified", "access is denied", "permission denied",
        "cannot connect to the docker daemon", "error during connect", "no such image", "no such container",
        "oci runtime create failed", "failed to start container", "broken pipe", "error response from daemon"
    ];

    public void WriteCommand(DockerCommand command, DockerCommandResult result)
    {
        var operation = command.Arguments switch
        {
            ["info", ..] => "info",
            ["image", "inspect", ..] => "image inspect",
            ["container", "create", ..] => "container create",
            ["container", "ls", ..] => "container ls",
            ["container", "start", ..] => "container start",
            ["container", "inspect", "--format", "{{.State.Running}}", _] => "container inspect (QA running check)",
            ["container", "inspect", ..] => "container inspect",
            ["container", "rm", ..] => "container rm",
            _ => "unknown"
        };
        var marker = operation == "container start" ? DescribeReadyMarker(result.Error) : null;
        var stderr = DescribeStderr(result.Error, marker == "Prefix");
        var message = $"DockerOperation=\"{operation}\" End={result.End} ExitCode={result.ExitCode} " +
            $"StdoutBytes={result.Output.Length}/{command.OutputLimit} StderrBytes={result.Error.Length}/{command.ErrorLimit} " +
            $"StderrHint=\"{stderr}\"";
        if (marker is not null) message += $" Stdin={result.Stdin} ReadyMarker={marker}";
        if (result.NativeStartError is { } code) message += $" NativeStartError={code}";
        write(message);

        // The dedicated test opts in for a fixed fixture. Never use this path for general attach output.
        if (!showPythonAcceptedStderr) return;
        if (command.Arguments is ["container", "create", .., "/bin/sh", "-c", var script, "coderank", _, "0"])
            write($"PythonAcceptedCreate ScriptCR={script.Count(c => c == '\r')} ScriptLF={script.Count(c => c == '\n')}");
        if (command.Arguments is ["container", "start", "--attach", "--interactive", _] &&
            command.Payload.Span.SequenceEqual(PythonAcceptedSource) && command.Input.IsEmpty)
        {
            var bytes = result.Error.AsSpan(0, Math.Min(RawStderrSampleBytes, result.Error.Length));
            var escaped = JsonSerializer.Serialize(Encoding.UTF8.GetString(bytes));
            write($"PythonAcceptedStderr End={result.End} ExitCode={result.ExitCode} Stdin={result.Stdin} " +
                $"Bytes={result.Error.Length} ShownBytes={bytes.Length} Text={escaped} Hex={Convert.ToHexString(bytes)}");
        }
    }

    private static string DescribeReadyMarker(ReadOnlySpan<byte> stderr)
    {
        var offset = stderr.IndexOf(Ready);
        if (offset == 0) return "Prefix";
        var crlfOffset = stderr.IndexOf("coderank-docker-ready-v1\r\n"u8);
        if (crlfOffset == 0) return "CrlfPrefix";
        if (offset > 0) return "AfterOtherBytes";
        return crlfOffset > 0 ? "CrlfAfterOtherBytes" : "Missing";
    }

    private static string DescribeStderr(ReadOnlySpan<byte> stderr, bool attachedProgramStarted)
    {
        if (stderr.IsEmpty) return "empty";
        if (attachedProgramStarted) return "attached output withheld";

        // Even stderr before the marker can contain untrusted bytes. Emit only known phrases.
        var sample = Encoding.ASCII.GetString(stderr[..Math.Min(StderrSampleBytes, stderr.Length)]);
        return StderrHints.FirstOrDefault(hint => sample.Contains(hint, StringComparison.OrdinalIgnoreCase))
            ?? "unrecognized output withheld";
    }

    public bool IsEnabled(LogLevel level) => level is >= LogLevel.Debug and < LogLevel.None;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(level)) write(formatter(state, null));
    }
}

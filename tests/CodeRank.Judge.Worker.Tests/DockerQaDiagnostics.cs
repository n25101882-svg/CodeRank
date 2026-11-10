using System.Text;
using CodeRank.Judge.Worker;
using Microsoft.Extensions.Logging;

namespace CodeRank.Judge.Worker.Tests;

internal sealed class DockerQaDiagnostics(Action<string> write) : ILogger<DockerExecutionProvider>
{
    private const int StderrSampleBytes = 1024;
    private static readonly byte[] Ready = Encoding.ASCII.GetBytes(DockerExecutionProvider.Ready);
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

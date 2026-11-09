using System.Net.Sockets;
using CodeRank.Judge.Protocol;

namespace CodeRank.Judge.Worker;

internal sealed class SubmissionGrader(IIsolatedJudgeRunner sandbox)
{
    public const string SystemError = "SystemError";

    public async Task<string> GradeAsync(JudgePayload payload, CancellationToken token)
    {
        IReadOnlyList<IsolatedCaseResult> results;
        try
        {
            results = await sandbox.RunAsync(payload.Language, payload.SourceCode,
                payload.TestCases.Select(test => test.Input).ToArray(), token);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or SocketException or
            UnauthorizedAccessException or InvalidOperationException or NotSupportedException or TimeoutException ||
            (error is OperationCanceledException && !token.IsCancellationRequested))
        {
            return SystemError;
        }

        token.ThrowIfCancellationRequested();
        // Infrastructure failures take precedence over results produced for any earlier case.
        if (results.Count == 0 || results.Count != payload.TestCases.Count ||
            results.Any(result => result.Status == IsolatedCaseStatus.SystemError))
            return SystemError;

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            if (result.Status != IsolatedCaseStatus.Exited)
                return result.Status switch
                {
                    IsolatedCaseStatus.CompilationError => "CompilationError",
                    IsolatedCaseStatus.RuntimeError or IsolatedCaseStatus.OutputLimitExceeded => "RuntimeError",
                    IsolatedCaseStatus.TimeLimitExceeded => "TimeLimitExceeded",
                    IsolatedCaseStatus.MemoryLimitExceeded => "MemoryLimitExceeded",
                    _ => SystemError
                };
            if (!NormalizeOutput(result.StandardOutput).SequenceEqual(NormalizeOutput(payload.TestCases[i].ExpectedOutput)))
                return "WrongAnswer";
        }
        return "Accepted";
    }

    private static ReadOnlySpan<char> NormalizeOutput(string output)
    {
        var normalized = output.Replace("\r\n", "\n", StringComparison.Ordinal);
        // Preserve every space and blank line except one optional final line ending.
        return normalized.EndsWith('\n') ? normalized.AsSpan(0, normalized.Length - 1) : normalized.AsSpan();
    }
}

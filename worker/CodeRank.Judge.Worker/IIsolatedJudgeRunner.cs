namespace CodeRank.Judge.Worker;

// The future trusted adapter compiles once and supplies only the current input to each isolated process.
internal interface IIsolatedJudgeRunner
{
    Task<IReadOnlyList<IsolatedCaseResult>> RunAsync(string language, string sourceCode, IReadOnlyList<string> inputs,
        CancellationToken token);
}

internal enum IsolatedCaseStatus
{
    Exited, CompilationError, RuntimeError, TimeLimitExceeded, MemoryLimitExceeded, OutputLimitExceeded, SystemError
}

internal sealed class IsolatedCaseResult
{
    public required IsolatedCaseStatus Status { get; init; }
    public required string StandardOutput { get; init; }
}

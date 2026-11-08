using System.Buffers.Binary;
using System.ComponentModel;
using System.Text;
using CodeRank.Judge.Protocol;
using CodeRank.Judge.Worker;

namespace CodeRank.Judge.Sandbox;

internal interface ISandboxExecutor
{
    Task<SandboxExecution> ExecuteAsync(SandboxPhase phase, byte[] payload, byte[] input, SandboxBudget budget,
        CancellationToken token);
}

internal sealed record SandboxExecution(IsolatedCaseStatus Status, byte[] Output)
{
    public static SandboxExecution Error { get; } = new(IsolatedCaseStatus.SystemError, []);
}

internal sealed class NsJailRunner(ISandboxExecutor executor, SandboxLimits limits) : IIsolatedJudgeRunner
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly SemaphoreSlim Gate = new(1);
    private bool unavailable;

    public async Task<IReadOnlyList<IsolatedCaseResult>> RunAsync(string language, string sourceCode,
        IReadOnlyList<string> inputs, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var phase = language switch
        {
            "cpp17" => SandboxPhase.Cpp17,
            "python3" => SandboxPhase.Python3,
            _ => throw new ArgumentException("Unsupported judge language.", nameof(language))
        };
        if (inputs.Count is < 1 or > JudgePayloadContract.MaxTests || string.IsNullOrWhiteSpace(sourceCode))
            throw new ArgumentException("A program and at least one test input are required.");
        var payload = Encode(sourceCode, JudgePayloadContract.MaxSourceBytes);
        var encodedInputs = inputs.Select(input => Encode(input, JudgePayloadContract.MaxTestFieldBytes)).ToArray();
        if (encodedInputs.Sum(input => input.Length) > JudgePayloadContract.MaxTestSetBytes)
            throw new ArgumentException("Test inputs exceed the total byte limit.", nameof(inputs));

        await Gate.WaitAsync(token);
        try
        {
            if (unavailable) return Repeat(IsolatedCaseStatus.SystemError, inputs.Count);
            if (phase == SandboxPhase.Cpp17)
            {
                var compile = await ExecuteAsync(SandboxPhase.Compile, payload, [], token);
                if (compile.Status != IsolatedCaseStatus.Exited) return Repeat(compile.Status, inputs.Count);
                if (!IsExecutableElf(compile.Output))
                {
                    unavailable = true;
                    return Repeat(IsolatedCaseStatus.SystemError, inputs.Count);
                }
                payload = compile.Output;
            }

            var results = new List<IsolatedCaseResult>(inputs.Count);
            foreach (var input in encodedInputs)
            {
                var result = unavailable ? SandboxExecution.Error : await ExecuteAsync(phase, payload, input, token);
                results.Add(ToCaseResult(result));
            }
            return results;
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<SandboxExecution> ExecuteAsync(SandboxPhase phase, byte[] payload, byte[] input,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var result = await executor.ExecuteAsync(phase, payload, input, limits.For(phase), token);
            if (result.Status == IsolatedCaseStatus.SystemError) unavailable = true;
            return result;
        }
        catch (Exception error) when (error is SandboxUnavailableException or IOException or UnauthorizedAccessException
            or Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            unavailable = true;
            return SandboxExecution.Error;
        }
    }

    private static IsolatedCaseResult ToCaseResult(SandboxExecution result)
    {
        if (result.Status != IsolatedCaseStatus.Exited) return Case(result.Status);
        try { return Case(result.Status, Utf8.GetString(result.Output)); }
        catch (DecoderFallbackException) { return Case(IsolatedCaseStatus.RuntimeError); }
    }

    private static byte[] Encode(string value, int maxBytes)
    {
        if (value is null || value.Length > maxBytes) throw new ArgumentException("Judge input exceeds its byte limit.");
        try
        {
            if (Utf8.GetByteCount(value) > maxBytes) throw new ArgumentException("Judge input exceeds its byte limit.");
            return Utf8.GetBytes(value);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("Judge input is not valid Unicode.");
        }
    }

    private static bool IsExecutableElf(ReadOnlySpan<byte> bytes) =>
        bytes.Length is >= 64 and <= SandboxLimits.ArtifactBytes && bytes[..6].SequenceEqual("\u007fELF\u0002\u0001"u8) &&
        BinaryPrimitives.ReadUInt16LittleEndian(bytes[16..]) is 2 or 3 &&
        BinaryPrimitives.ReadUInt16LittleEndian(bytes[18..]) == 62;

    private static IsolatedCaseResult[] Repeat(IsolatedCaseStatus status, int count) =>
        Enumerable.Repeat(Case(status), count).ToArray();

    private static IsolatedCaseResult Case(IsolatedCaseStatus status, string output = "") =>
        new() { Status = status, StandardOutput = output };
}

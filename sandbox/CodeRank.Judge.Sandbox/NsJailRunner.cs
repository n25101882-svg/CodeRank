using System.Buffers.Binary;
using System.Text;
using CodeRank.Judge.Protocol;
using CodeRank.Judge.Worker;

namespace CodeRank.Judge.Sandbox;

internal interface ISandboxExecutor
{
    Task<SandboxExecution> ExecuteAsync(SandboxPhase phase, byte[] payload, byte[] input, SandboxBudget budget,
        CancellationToken token);
}

internal sealed record SandboxExecution(IsolatedCaseStatus Status, byte[] Output, SandboxFailure? Failure = null)
{
    public static SandboxExecution Error { get; } = new(IsolatedCaseStatus.SystemError, []);
}

internal sealed class NsJailRunner(ISandboxExecutor executor, SandboxLimits limits, Action<SandboxFailure>? report = null)
    : IIsolatedJudgeRunner
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly SemaphoreSlim Gate = new(1);
    private bool unavailable;
    public bool IsUnavailable => unavailable;

    public async Task<IReadOnlyList<IsolatedCaseResult>> RunAsync(string language, string sourceCode,
        IReadOnlyList<string> inputs, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (inputs.Count is < 1 or > JudgePayloadContract.MaxTests)
            throw new ArgumentException("At least one test input is required.", nameof(inputs));
        var encodedInputs = inputs.Select(input => Encode(input, JudgePayloadContract.MaxTestFieldBytes)).ToArray();
        if (encodedInputs.Sum(input => input.Length) > JudgePayloadContract.MaxTestSetBytes)
            throw new ArgumentException("Test inputs exceed the total byte limit.", nameof(inputs));

        using var session = await OpenAsync(language, sourceCode, token);
        if (session.Status != IsolatedCaseStatus.Exited) return Repeat(session.Status, inputs.Count);
        var results = new List<IsolatedCaseResult>(inputs.Count);
        foreach (var input in encodedInputs)
            results.Add(await session.RunCaseAsync(input, token));
        return results;
    }

    public async Task<Session> OpenAsync(string language, string sourceCode, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var phase = language switch
        {
            "cpp17" => SandboxPhase.Cpp17,
            "python3" => SandboxPhase.Python3,
            _ => throw new ArgumentException("Unsupported judge language.", nameof(language))
        };
        if (string.IsNullOrWhiteSpace(sourceCode)) throw new ArgumentException("A program is required.");
        var payload = Encode(sourceCode, JudgePayloadContract.MaxSourceBytes);

        await Gate.WaitAsync(token);
        try
        {
            if (unavailable) return new(this, phase, [], IsolatedCaseStatus.SystemError);
            if (phase == SandboxPhase.Cpp17)
            {
                var compile = await ExecuteAsync(SandboxPhase.Compile, payload, [], token);
                if (compile.Status != IsolatedCaseStatus.Exited) return new(this, phase, [], compile.Status);
                if (!IsExecutableElf(compile.Output))
                {
                    Disable(new(new(SandboxStage.Artifact, SandboxFault.InvalidData)));
                    return new(this, phase, [], IsolatedCaseStatus.SystemError);
                }
                payload = compile.Output;
            }

            return new(this, phase, payload, IsolatedCaseStatus.Exited);
        }
        catch
        {
            Gate.Release();
            throw;
        }
    }

    internal sealed class Session(NsJailRunner owner, SandboxPhase phase, byte[] payload, IsolatedCaseStatus status)
        : IDisposable
    {
        private int cases;
        private int inputBytes;
        private bool disposed;

        public IsolatedCaseStatus Status { get; } = status;

        public async Task<IsolatedCaseResult> RunCaseAsync(byte[] input, CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            token.ThrowIfCancellationRequested();
            if (Status != IsolatedCaseStatus.Exited || input.Length > JudgePayloadContract.MaxTestFieldBytes ||
                cases >= JudgePayloadContract.MaxTests || inputBytes + input.Length > JudgePayloadContract.MaxTestSetBytes)
                throw new InvalidDataException("Judge session input limits were exceeded.");
            cases++;
            inputBytes += input.Length;
            var result = owner.unavailable ? SandboxExecution.Error : await owner.ExecuteAsync(phase, payload, input, token);
            return ToCaseResult(result);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
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
            if (result.Status == IsolatedCaseStatus.SystemError)
                Disable(result.Failure ?? new(new(SandboxStage.Runner)));
            return result;
        }
        catch (Exception error) when (SandboxFailure.IsInfrastructureError(error))
        {
            Disable(SandboxFailure.From(SandboxStage.Runner, error));
            return SandboxExecution.Error;
        }
    }

    private void Disable(SandboxFailure failure)
    {
        if (unavailable) return;
        unavailable = true;
        report?.Invoke(failure);
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

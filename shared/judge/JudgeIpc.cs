using System.Buffers.Binary;
using System.Text;
using CodeRank.Judge.Worker;

namespace CodeRank.Judge.Protocol;

internal enum JudgeIpcKind : byte { Start = 1, Input, End, Ready, Result }

internal readonly record struct JudgeIpcFrame(JudgeIpcKind Kind, byte[] Payload);

internal static class JudgeIpc
{
    public const string SocketPath = "/run/coderank-judge-ipc/sandbox.sock";
    public const string SocketName = "judge-sandbox";
    public const string WorkerAccount = "coderank-worker";
    public const int MaxStartBytes = JudgePayloadContract.MaxSourceBytes + 2;
    public const int MaxOutputBytes = 64 * 1024;
    public static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan PrepareTimeout = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan CaseTimeout = TimeSpan.FromSeconds(25);
    public static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(15);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const byte Version = 1;

    public static async ValueTask<JudgeIpcFrame> ReadAsync(Stream stream, int maximum, CancellationToken token)
    {
        var header = new byte[5];
        await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        var kind = (JudgeIpcKind)header[0];
        if (length < 0 || length > maximum || !Enum.IsDefined(kind))
            throw InvalidFrame();
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, token);
        return new(kind, payload);
    }

    public static async ValueTask WriteAsync(Stream stream, JudgeIpcKind kind, ReadOnlyMemory<byte> payload,
        CancellationToken token)
    {
        if (payload.Length > MaxStartBytes) throw InvalidFrame();
        var header = new byte[5];
        header[0] = (byte)kind;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), payload.Length);
        await stream.WriteAsync(header, token);
        if (!payload.IsEmpty) await stream.WriteAsync(payload, token);
    }

    public static byte[] EncodeStart(string language, string sourceCode)
    {
        var source = EncodeText(sourceCode, JudgePayloadContract.MaxSourceBytes);
        var payload = new byte[source.Length + 2];
        payload[0] = Version;
        payload[1] = language switch { "cpp17" => 1, "python3" => 2, _ => throw InvalidFrame() };
        source.CopyTo(payload, 2);
        return payload;
    }

    public static (string Language, string SourceCode) DecodeStart(JudgeIpcFrame frame)
    {
        var bytes = frame.Payload;
        if (frame.Kind != JudgeIpcKind.Start || bytes.Length < 3 || bytes[0] != Version)
            throw InvalidFrame();
        var language = bytes[1] switch { 1 => "cpp17", 2 => "python3", _ => throw InvalidFrame() };
        var source = DecodeText(bytes.AsSpan(2));
        if (string.IsNullOrWhiteSpace(source)) throw InvalidFrame();
        return (language, source);
    }

    public static byte[] EncodeResult(IsolatedCaseResult result)
    {
        var output = result.Status == IsolatedCaseStatus.Exited ? EncodeText(result.StandardOutput, MaxOutputBytes) : [];
        var payload = new byte[output.Length + 1];
        payload[0] = (byte)result.Status;
        output.CopyTo(payload, 1);
        return payload;
    }

    public static IsolatedCaseResult DecodeResult(JudgeIpcFrame frame)
    {
        if (frame.Kind != JudgeIpcKind.Result || frame.Payload.Length == 0) throw InvalidFrame();
        var status = (IsolatedCaseStatus)frame.Payload[0];
        if (!Enum.IsDefined(status) || (status != IsolatedCaseStatus.Exited && frame.Payload.Length != 1))
            throw InvalidFrame();
        return new() { Status = status, StandardOutput = DecodeText(frame.Payload.AsSpan(1)) };
    }

    public static byte[] EncodeText(string value, int maximum)
    {
        try
        {
            if (value.Length > maximum || Utf8.GetByteCount(value) > maximum) throw InvalidFrame();
            return Utf8.GetBytes(value);
        }
        catch (EncoderFallbackException) { throw InvalidFrame(); }
    }

    public static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        try { return Utf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw InvalidFrame(); }
    }

    public static CancellationTokenSource Deadline(TimeSpan timeout, CancellationToken token)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        return deadline;
    }

    private static InvalidDataException InvalidFrame() => new("Invalid Judge IPC frame.");
}

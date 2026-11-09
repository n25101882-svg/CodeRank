using System.Net.Sockets;
using CodeRank.Judge.Protocol;

namespace CodeRank.Judge.Worker;

internal sealed class UnixJudgeRunner : IIsolatedJudgeRunner
{
    public async Task<IReadOnlyList<IsolatedCaseResult>> RunAsync(string language, string sourceCode,
        IReadOnlyList<string> inputs, CancellationToken token)
    {
        using var session = JudgeIpc.Deadline(JudgeIpc.SessionTimeout, token);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using (var connect = JudgeIpc.Deadline(JudgeIpc.IoTimeout, session.Token))
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(JudgeIpc.SocketAddress), connect.Token);
        // PID 1 creates and listens on the protected systemd pathname socket.
        if (!LinuxJudgeIdentity.IsPeer(socket, uid: 0, pid: 1))
            throw new InvalidDataException("Untrusted Judge IPC listener.");

        using var stream = new NetworkStream(socket);
        return await ExchangeAsync(stream, language, sourceCode, inputs, session.Token);
    }

    internal static async Task<IReadOnlyList<IsolatedCaseResult>> ExchangeAsync(Stream stream, string language,
        string sourceCode, IReadOnlyList<string> inputs, CancellationToken token)
    {
        if (inputs.Count is < 1 or > JudgePayloadContract.MaxTests)
            throw new InvalidDataException("Invalid Judge IPC test count.");
        using (var send = JudgeIpc.Deadline(JudgeIpc.IoTimeout, token))
            await JudgeIpc.WriteAsync(stream, JudgeIpcKind.Start, JudgeIpc.EncodeStart(language, sourceCode), send.Token);

        JudgeIpcFrame ready;
        using (var prepare = JudgeIpc.Deadline(JudgeIpc.PrepareTimeout, token))
            ready = await JudgeIpc.ReadAsync(stream, 1, prepare.Token);
        if (ready is not { Kind: JudgeIpcKind.Ready, Payload.Length: 0 })
        {
            var failure = JudgeIpc.DecodeResult(ready);
            if (failure.Status == IsolatedCaseStatus.Exited) throw new InvalidDataException("Invalid Judge IPC readiness.");
            return Enumerable.Repeat(failure, inputs.Count).ToArray();
        }

        var results = new List<IsolatedCaseResult>(inputs.Count);
        foreach (var input in inputs)
        {
            using var run = JudgeIpc.Deadline(JudgeIpc.CaseTimeout, token);
            await JudgeIpc.WriteAsync(stream, JudgeIpcKind.Input, JudgeIpc.EncodeText(input, JudgePayloadContract.MaxTestFieldBytes),
                run.Token);
            var result = JudgeIpc.DecodeResult(await JudgeIpc.ReadAsync(stream, JudgeIpc.MaxOutputBytes + 1, run.Token));
            results.Add(result);
            if (result.Status == IsolatedCaseStatus.SystemError)
            {
                results.AddRange(Enumerable.Repeat(result, inputs.Count - results.Count));
                return results;
            }
        }
        using var end = JudgeIpc.Deadline(JudgeIpc.IoTimeout, token);
        await JudgeIpc.WriteAsync(stream, JudgeIpcKind.End, ReadOnlyMemory<byte>.Empty, end.Token);
        return results;
    }
}

using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using CodeRank.Judge.Protocol;
using CodeRank.Judge.Worker;

namespace CodeRank.Judge.Sandbox;

internal sealed class SandboxIpcServer(NsJailRunner runner)
{
    public async Task<int> RunAsync(CancellationToken token)
    {
        var workerUid = LinuxJudgeIdentity.ReadServiceUid(JudgeIpc.WorkerAccount);
        if (workerUid == LinuxJudgeIdentity.GetEffectiveUid())
            throw new SandboxUnavailableException(SandboxStage.HostPrivileges);

        using var listener = OpenActivatedListener();
        while (!token.IsCancellationRequested)
        {
            using var peer = await listener.AcceptAsync(token);
            if (!LinuxJudgeIdentity.IsPeer(peer, workerUid)) continue;
            try { await ServePeerAsync(peer, token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                Console.Error.WriteLine("Judge IPC session canceled or timed out.");
            }
            catch (Exception error) when (error is IOException or InvalidDataException or SocketException)
            {
                Console.Error.WriteLine("Judge IPC session closed or rejected.");
            }
            if (runner.IsUnavailable) return 1;
        }
        return 0;
    }

    private async Task ServePeerAsync(Socket peer, CancellationToken token)
    {
        using var sessionDeadline = JudgeIpc.Deadline(JudgeIpc.SessionTimeout, token);
        var sessionToken = sessionDeadline.Token;
        using var stream = new NetworkStream(peer);
        var probe = new byte[1];
        ValueTask<int> PeekPeer(CancellationToken ct) => peer.ReceiveAsync(probe, SocketFlags.Peek, ct);
        var (language, source) = JudgeIpc.DecodeStart(await ReadAsync(stream, JudgeIpc.MaxStartBytes, sessionToken));
        using var session = await WhileConnectedAsync(PeekPeer, ct => runner.OpenAsync(language, source, ct), sessionToken);
        if (session.Status != IsolatedCaseStatus.Exited)
        {
            await WriteResultAsync(stream, new() { Status = session.Status, StandardOutput = "" }, sessionToken);
            return;
        }

        using (var readyDeadline = JudgeIpc.Deadline(JudgeIpc.IoTimeout, sessionToken))
            await JudgeIpc.WriteAsync(stream, JudgeIpcKind.Ready, ReadOnlyMemory<byte>.Empty, readyDeadline.Token);

        while (true)
        {
            var frame = await ReadAsync(stream, JudgePayloadContract.MaxTestFieldBytes, sessionToken);
            if (frame is { Kind: JudgeIpcKind.End, Payload.Length: 0 }) return;
            if (frame.Kind != JudgeIpcKind.Input) throw new InvalidDataException("Expected a Judge IPC input.");
            _ = JudgeIpc.DecodeText(frame.Payload);
            var result = await WhileConnectedAsync(PeekPeer, ct => session.RunCaseAsync(frame.Payload, ct), sessionToken);
            await WriteResultAsync(stream, result, sessionToken);
            if (runner.IsUnavailable) return;
        }
    }

    internal static async Task<T> WhileConnectedAsync<T>(Func<CancellationToken, ValueTask<int>> peek,
        Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var watchStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var watch = WatchPeerAsync(peek, operation, watchStop.Token);
        try { return await action(operation.Token); }
        finally
        {
            await watchStop.CancelAsync();
            await watch;
        }
    }

    private static async Task WatchPeerAsync(Func<CancellationToken, ValueTask<int>> peek,
        CancellationTokenSource operation, CancellationToken token)
    {
        // The worker waits for the result before sending another frame. EOF or a pipelined frame cancels this case.
        try { _ = await peek(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (SocketException) { }
        if (!token.IsCancellationRequested) await operation.CancelAsync();
    }

    private static async ValueTask<JudgeIpcFrame> ReadAsync(Stream stream, int maximum, CancellationToken token)
    {
        using var deadline = JudgeIpc.Deadline(JudgeIpc.IoTimeout, token);
        return await JudgeIpc.ReadAsync(stream, maximum, deadline.Token);
    }

    private static async ValueTask WriteResultAsync(Stream stream, IsolatedCaseResult result, CancellationToken token)
    {
        using var deadline = JudgeIpc.Deadline(JudgeIpc.IoTimeout, token);
        await JudgeIpc.WriteAsync(stream, JudgeIpcKind.Result, JudgeIpc.EncodeResult(result), deadline.Token);
    }

    private static Socket OpenActivatedListener()
    {
        const int descriptor = 3;
        const int setDescriptorFlags = 2;
        const int closeOnExec = 1;
        if (Environment.GetEnvironmentVariable("LISTEN_PID") != Environment.ProcessId.ToString(CultureInfo.InvariantCulture) ||
            Environment.GetEnvironmentVariable("LISTEN_FDS") != "1" ||
            Environment.GetEnvironmentVariable("LISTEN_FDNAMES") != JudgeIpc.SocketName ||
            FileControl(descriptor, setDescriptorFlags, closeOnExec) != 0)
            throw new SandboxUnavailableException(SandboxStage.Configuration);

        var handle = new SafeSocketHandle(new IntPtr(descriptor), ownsHandle: true);
        Socket? listener = null;
        try
        {
            listener = new Socket(handle);
            if (listener.AddressFamily != AddressFamily.Unix || listener.SocketType != SocketType.Stream ||
                listener.LocalEndPoint?.ToString() != JudgeIpc.SocketPath ||
                listener.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.AcceptConnection) is not 1)
                throw new SandboxUnavailableException(SandboxStage.Configuration);
            return listener;
        }
        catch
        {
            listener?.Dispose();
            handle.Dispose();
            throw;
        }
    }

    [DllImport("libc", EntryPoint = "fcntl")]
    private static extern int FileControl(int descriptor, int command, int flags);
}

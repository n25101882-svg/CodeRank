using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace CodeRank.Judge.Protocol;

internal static class LinuxJudgeIdentity
{
    private const int SolSocket = 1;
    private const int SoPeerCred = 17;

    public static bool IsPeer(Socket socket, uint uid, int? pid = null)
    {
        if (!OperatingSystem.IsLinux() || socket.AddressFamily != AddressFamily.Unix) return false;
        var size = (uint)Marshal.SizeOf<PeerCredentials>();
        return GetSocketOption(socket.SafeHandle, SolSocket, SoPeerCred, out var peer, ref size) == 0 &&
            size == Marshal.SizeOf<PeerCredentials>() && peer.Pid > 0 && peer.Uid == uid &&
            (!pid.HasValue || peer.Pid == pid.Value);
    }

    public static uint ReadServiceUid(string account)
    {
        // Deployment uses dedicated local system users.
        var user = File.ReadLines("/etc/passwd").SingleOrDefault(line =>
            line.StartsWith(account + ":", StringComparison.Ordinal));
        var fields = user?.Split(':');
        if (fields is not { Length: 7 } ||
            !uint.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var uid) || uid == 0)
            throw new InvalidOperationException("The dedicated Judge service user is required.");
        return uid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PeerCredentials
    {
        public int Pid;
        public uint Uid;
        public uint Gid;
    }

    [DllImport("libc", EntryPoint = "getsockopt")]
    private static extern int GetSocketOption(SafeSocketHandle descriptor, int level, int option,
        out PeerCredentials credentials, ref uint size);

    [DllImport("libc", EntryPoint = "geteuid")]
    public static extern uint GetEffectiveUid();
}

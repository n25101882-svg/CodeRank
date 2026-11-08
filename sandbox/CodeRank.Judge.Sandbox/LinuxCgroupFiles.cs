using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodeRank.Judge.Sandbox;

internal static class LinuxCgroupFiles
{
    private const int ReadOnly = 0;
    private const int WriteOnly = 1;
    private const int CloseOnExec = 0x80000;
    private const int NoFollow = 0x20000;
    private const int Interrupted = 4;
    private const int MaxReadBytes = 4096;

    internal static bool IsControlName(string? name) => name is
        "cgroup.type" or "cgroup.controllers" or "cgroup.subtree_control" or "cgroup.procs" or "cgroup.events" or
        "cgroup.kill" or "memory.max" or "memory.swap.max" or "memory.oom.group" or "memory.events" or
        "pids.max" or "cpu.max";

    public static string Read(string directory, string control, SandboxStage stage)
    {
        // Constructing a FileStream from a descriptor bypasses the path constructor's best-effort flock.
        using var handle = Open(directory, control, ReadOnly, stage);
        try
        {
            using var stream = new FileStream(handle, FileAccess.Read);
            Span<byte> buffer = stackalloc byte[MaxReadBytes + 1];
            var length = stream.ReadAtLeast(buffer, buffer.Length, false);
            if (length > MaxReadBytes) throw new SandboxUnavailableException(stage, SandboxFault.InvalidData, control: control);
            return Encoding.ASCII.GetString(buffer[..length]);
        }
        catch (Exception error) when (SandboxFailure.IsInfrastructureError(error) && error is not SandboxUnavailableException)
        {
            var diagnostic = SandboxFailure.From(stage, error).Cause with { Control = control };
            throw new SandboxUnavailableException(new SandboxFailure(diagnostic));
        }
    }

    public static void Write(string directory, string control, string value, SandboxStage stage)
    {
        using var handle = Open(directory, control, WriteOnly, stage);
        var bytes = Encoding.ASCII.GetBytes(value);
        nint written;
        int errno;
        do
        {
            written = WriteDescriptor(handle, bytes, (nuint)bytes.Length);
            errno = Marshal.GetLastPInvokeError();
        } while (written < 0 && errno == Interrupted);
        if (written < 0) throw new SandboxUnavailableException(stage, SandboxFault.Native, errno, control);
        // cgroup expects the whole setting in one write. A short write is a failure.
        if (written != bytes.Length) throw new SandboxUnavailableException(stage, SandboxFault.Io, control: control);
    }

    private static SafeFileHandle Open(string directory, string control, int access, SandboxStage stage)
    {
        if (!IsControlName(control)) throw new SandboxUnavailableException(stage, SandboxFault.InvalidConfiguration);
        var descriptor = OpenDescriptor(directory + "/" + control, access | CloseOnExec | NoFollow);
        if (descriptor < 0) throw new SandboxUnavailableException(stage, SandboxFault.Native, Marshal.GetLastPInvokeError(), control);
        return new SafeFileHandle(descriptor, ownsHandle: true);
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenDescriptor([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern nint WriteDescriptor(SafeFileHandle handle, byte[] bytes, nuint count);
}

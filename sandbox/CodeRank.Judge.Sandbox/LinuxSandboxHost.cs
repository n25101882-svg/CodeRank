using System.Globalization;
using System.Runtime.InteropServices;

namespace CodeRank.Judge.Sandbox;

internal static class LinuxSandboxHost
{
    private const long Cgroup2Magic = 0x63677270;
    private const int AtFdcwd = -100;
    private const int AtSymlinkNofollow = 0x100;
    private const uint StatTypeModeUid = 0x000b;
    private const int ModeType = 0xf000;
    private const int DirectoryMode = 0x4000;
    private const int FileMode = 0x8000;

    public static void VerifyPlatform()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            GetEffectiveUid() == 0 ||
            File.ReadAllText("/proc/self/attr/current").TrimEnd('\0', '\r', '\n') != SandboxLayout.Profile + " (enforce)" ||
            File.ReadAllText("/proc/sys/kernel/apparmor_restrict_unprivileged_userns").Trim() != "1" ||
            !HasStatus("NoNewPrivs:", "1") || !HasStatus("CapEff:", "0000000000000000") ||
            !HasStatus("CapBnd:", "0000000000000000"))
            throw new SandboxUnavailableException();
    }

    public static SandboxLimits LoadLimits()
    {
        VerifyPlatform();
        var node = ReadNode(SandboxLayout.LimitsFile);
        if (node is null) return new();
        RequireTrusted(SandboxLayout.LimitsFile, directory: false);
        using var file = File.OpenRead(SandboxLayout.LimitsFile);
        if (file.Length is < 2 or > 4096) throw new SandboxUnavailableException();
        using var reader = new StreamReader(file);
        return SandboxLimits.Parse(reader.ReadToEnd());
    }

    public static void Verify(NsJailPlan plan)
    {
        VerifyPlatform();
        if (ReadStatFs(SandboxLayout.CgroupMount, out var fs) != 0 || fs.Type != Cgroup2Magic ||
            File.ReadAllText("/proc/self/cgroup").Trim() != "0::" + SandboxLayout.UnitCgroup + "/supervisor" ||
            File.ReadAllText(SandboxLayout.Cgroup + "/cgroup.type").Trim() != "domain" ||
            File.ReadAllText(SandboxLayout.Cgroup + "/supervisor/cgroup.procs").Trim() !=
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture) ||
            Directory.EnumerateDirectories(SandboxLayout.Cgroup, "job-*").Any())
            throw new SandboxUnavailableException();

        var scratch = ReadNode(SandboxLayout.Scratch);
        if (scratch is not { } dir || (dir.Mode & ModeType) != DirectoryMode || dir.Uid != GetEffectiveUid() ||
            (dir.Mode & 0x1ff) != 0x1c0 || Directory.EnumerateFileSystemEntries(SandboxLayout.Scratch, "job-*").Any())
            throw new SandboxUnavailableException();

        RequireTrusted(SandboxLayout.Launcher, directory: false);
        RequireTrusted(SandboxLayout.NsJail, directory: false);
        RequireTrusted(plan.Root, directory: true);
        RequireTrusted(plan.Root + "/job/coderank-sandbox-entry", directory: false);
        RequireTrusted(plan.Root + "/job/" + plan.PayloadName, directory: false);
        if (HasMountedPath(File.ReadLines("/proc/self/mountinfo"), plan.Root))
            throw new SandboxUnavailableException();
    }

    internal static bool HasMountedPath(IEnumerable<string> mountInfo, string root) =>
        mountInfo.Select(line => line.Split(' ', 6)).Any(fields => fields.Length > 4 &&
            (fields[4] == root || fields[4].StartsWith(root + "/", StringComparison.Ordinal)));

    private static bool HasStatus(string key, string value) => File.ReadLines("/proc/self/status")
        .Any(line => line.StartsWith(key, StringComparison.Ordinal) && line[key.Length..].Trim() == value);

    private static void RequireTrusted(string path, bool directory)
    {
        for (var node = path; node != "/"; node = Path.GetDirectoryName(node)!)
        {
            var info = ReadNode(node);
            var expectedType = directory ? DirectoryMode : FileMode;
            // Require root ownership, reject symlinks, and reject set-ID or group/world write.
            if (info is not { Uid: 0 } file || (file.Mode & ModeType) != expectedType || (file.Mode & 0x0c12) != 0)
                throw new SandboxUnavailableException();
            directory = true;
        }
    }

    private static Statx? ReadNode(string path)
    {
        if (ReadStatx(AtFdcwd, path, AtSymlinkNofollow, StatTypeModeUid, out var info) == 0)
        {
            if ((info.Mask & StatTypeModeUid) != StatTypeModeUid) throw new SandboxUnavailableException();
            return info;
        }
        if (Marshal.GetLastPInvokeError() == 2) return null;
        throw new SandboxUnavailableException();
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(28)] public ushort Mode;
    }

    [StructLayout(LayoutKind.Explicit, Size = 120)]
    private struct StatFs
    {
        [FieldOffset(0)] public long Type;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int ReadStatx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out Statx info);

    [DllImport("libc", EntryPoint = "statfs", SetLastError = true)]
    private static extern int ReadStatFs([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out StatFs info);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUid();
}

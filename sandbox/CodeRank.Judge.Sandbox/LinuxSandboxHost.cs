using System.Globalization;
using System.Runtime.InteropServices;
using CodeRank.Judge.Protocol;

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
        var stage = SandboxStage.Platform;
        try
        {
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new SandboxUnavailableException(stage);

            stage = SandboxStage.AppArmor;
            if (File.ReadAllText("/proc/self/attr/current").TrimEnd('\0', '\r', '\n') != SandboxLayout.Profile + " (enforce)" ||
                File.ReadAllText("/proc/sys/kernel/apparmor_restrict_unprivileged_userns").Trim() != "1")
                throw new SandboxUnavailableException(stage);

            stage = SandboxStage.HostPrivileges;
            if (LinuxJudgeIdentity.GetEffectiveUid() == 0 || !HasStatus("NoNewPrivs:", "1") ||
                !HasStatus("CapEff:", "0000000000000000") || !HasStatus("CapBnd:", "0000000000000000"))
                throw new SandboxUnavailableException(stage);
        }
        catch (Exception error) when (SandboxFailure.IsInfrastructureError(error) && error is not SandboxUnavailableException)
        {
            throw new SandboxUnavailableException(SandboxFailure.From(stage, error));
        }
    }

    public static SandboxLimits LoadLimits()
    {
        VerifyPlatform();
        var node = ReadNode(SandboxLayout.LimitsFile, SandboxStage.Configuration);
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
        var layout = plan.Layout;
        var stage = SandboxStage.CgroupDelegation;
        try
        {
            if (ReadStatFs(SandboxLayout.CgroupMount, out var fs) != 0)
                throw new SandboxUnavailableException(stage, SandboxFault.Native, Marshal.GetLastPInvokeError());
            if (fs.Type != Cgroup2Magic ||
                File.ReadAllText("/proc/self/cgroup").Trim() != "0::" + layout.UnitCgroup + "/supervisor" ||
                LinuxCgroupFiles.Read(layout.Cgroup, "cgroup.type", stage).Trim() != "domain" ||
                LinuxCgroupFiles.Read(layout.Cgroup + "/supervisor", "cgroup.procs", stage).Trim() !=
                    Environment.ProcessId.ToString(CultureInfo.InvariantCulture) ||
                Directory.EnumerateDirectories(layout.Cgroup, "job-*").Any())
                throw new SandboxUnavailableException(stage);

            stage = SandboxStage.ScratchPreparation;
            var scratch = ReadNode(layout.Scratch, stage);
            if (scratch is not { } dir || (dir.Mode & ModeType) != DirectoryMode ||
                dir.Uid != LinuxJudgeIdentity.GetEffectiveUid() || (dir.Mode & 0x1ff) != 0x1c0 || HasScratchFiles(layout))
                throw new SandboxUnavailableException(stage);

            stage = SandboxStage.Installation;
            RequireTrusted(SandboxLayout.Launcher, directory: false);
            RequireTrusted(SandboxLayout.NsJail, directory: false);
            RequireTrusted(plan.Root, directory: true);
            RequireTrusted(plan.Root + "/job/coderank-sandbox-entry", directory: false);
            RequireTrusted(plan.Root + "/job/" + plan.PayloadName, directory: false);
            if (HasMountedPath(File.ReadLines("/proc/self/mountinfo"), plan.Root))
                throw new SandboxUnavailableException(stage);
        }
        catch (Exception error) when (SandboxFailure.IsInfrastructureError(error) && error is not SandboxUnavailableException)
        {
            throw new SandboxUnavailableException(SandboxFailure.From(stage, error));
        }
    }

    internal static bool HasScratchFiles(SandboxLayout layout) =>
        Directory.EnumerateFileSystemEntries(layout.Scratch, "job-*").Any() ||
        Directory.EnumerateFileSystemEntries(SandboxLayout.ExecutableScratch, SandboxLayout.ExecutablePrefix + "*").Any();

    internal static bool HasMountedPath(IEnumerable<string> mountInfo, string root) =>
        mountInfo.Select(line => line.Split(' ', 6)).Any(fields => fields.Length > 4 &&
            (fields[4] == root || fields[4].StartsWith(root + "/", StringComparison.Ordinal)));

    private static bool HasStatus(string key, string value) => File.ReadLines("/proc/self/status")
        .Any(line => line.StartsWith(key, StringComparison.Ordinal) && line[key.Length..].Trim() == value);

    private static void RequireTrusted(string path, bool directory)
    {
        for (var node = path; node != "/"; node = Path.GetDirectoryName(node)!)
        {
            var info = ReadNode(node, SandboxStage.Installation);
            var expectedType = directory ? DirectoryMode : FileMode;
            // Require root ownership, reject symlinks, and reject set-ID or group/world write.
            if (info is not { Uid: 0 } file || (file.Mode & ModeType) != expectedType || (file.Mode & 0x0c12) != 0)
                throw new SandboxUnavailableException(SandboxStage.Installation);
            directory = true;
        }
    }

    private static Statx? ReadNode(string path, SandboxStage stage)
    {
        if (ReadStatx(AtFdcwd, path, AtSymlinkNofollow, StatTypeModeUid, out var info) == 0)
        {
            if ((info.Mask & StatTypeModeUid) != StatTypeModeUid) throw new SandboxUnavailableException(stage);
            return info;
        }
        var errno = Marshal.GetLastPInvokeError();
        if (errno == 2) return null;
        throw new SandboxUnavailableException(stage, SandboxFault.Native, errno);
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

}

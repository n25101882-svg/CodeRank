using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace CodeRank.Judge.Sandbox;

internal enum SandboxStage
{
    Configuration, Platform, AppArmor, HostPrivileges, Installation, ScratchPreparation,
    CgroupDelegation, CgroupCreate, CgroupSetup, LauncherStart, LauncherBootstrap, LauncherCgroup,
    NsJailSetup, NsJailNamespace, NsJailMount, NsJailSeccomp,
    JailAttestation, JailMounts, JailSeccomp, JailProgramStart, Receipt,
    ProcessIo, ProcessCleanup, CgroupCleanup, ScratchCleanup, Artifact, Runner
}

internal enum SandboxFault { Rejected, AccessDenied, Io, Native, Timeout, ProcessState, InvalidConfiguration, InvalidData }

internal enum SandboxStderrStatus { Complete, LimitExceeded, Interrupted, Unavailable }

internal enum NsJailNamespaceDetail
{
    CloneEnvelope, CloneTimestamp, ClonePid, CloneCallSite, CloneMessage, CloneFlags, CloneErrno, CloneTruncated,
    ParentUserNamespace, ParentNetNamespace, ParentCgroupNamespace, NamespaceRecord,
    OpenUidMap, WriteUidMap, OpenGidMap, WriteGidMap, OpenSetgroups, WriteSetgroups
}

[Flags]
internal enum NsJailNamespaceFlags : ulong
{
    None = 0,
    CLONE_NEWTIME = 0x00000080,
    CLONE_NEWNS = 0x00020000,
    CLONE_NEWCGROUP = 0x02000000,
    CLONE_NEWUTS = 0x04000000,
    CLONE_NEWIPC = 0x08000000,
    CLONE_NEWUSER = 0x10000000,
    CLONE_NEWPID = 0x20000000,
    CLONE_NEWNET = 0x40000000
}

internal enum NsJailMountOperation
{
    BuildMountTree, FindWorkDir, PrepareMountTarget, MakeMountPrivate, Mount, Remount, PivotRoot, Umount2, Chdir,
    Fsopen, Fsconfig, Fsmount, MoveMount, OpenTree, MountSetattr, Statvfs
}

internal enum NsJailMountTarget { MountTree, Rootfs, Tmpfs, File }

internal enum NsJailErrno
{
    Unknown = 0, EPERM = 1, ENOENT = 2, ESRCH = 3, EINTR = 4, EIO = 5, ENXIO = 6, E2BIG = 7, EBADF = 9,
    EAGAIN = 11, ENOMEM = 12, EACCES = 13, EFAULT = 14, ENOTBLK = 15, EBUSY = 16, EEXIST = 17, EXDEV = 18,
    ENODEV = 19, ENOTDIR = 20, EISDIR = 21, EINVAL = 22, ENFILE = 23, EMFILE = 24, ENOSPC = 28, EROFS = 30,
    ENAMETOOLONG = 36, ENOSYS = 38, ENOTEMPTY = 39, ELOOP = 40, EUSERS = 87, EOPNOTSUPP = 95
}

internal readonly record struct SandboxDiagnostic(SandboxStage Stage, SandboxFault Fault = SandboxFault.Rejected,
    int Errno = 0, string? Control = null, NsJailNamespaceFlags? CloneFlags = null,
    NsJailNamespaceDetail? NamespaceDetail = null, SandboxStderrStatus? StderrStatus = null,
    NsJailMountOperation? MountOperation = null, NsJailMountTarget? MountTarget = null)
{
    private const NsJailNamespaceFlags KnownFlags = NsJailNamespaceFlags.CLONE_NEWTIME | NsJailNamespaceFlags.CLONE_NEWNS |
        NsJailNamespaceFlags.CLONE_NEWCGROUP | NsJailNamespaceFlags.CLONE_NEWUTS | NsJailNamespaceFlags.CLONE_NEWIPC |
        NsJailNamespaceFlags.CLONE_NEWUSER | NsJailNamespaceFlags.CLONE_NEWPID | NsJailNamespaceFlags.CLONE_NEWNET;

    public string Format(string prefix = "")
    {
        var message = $"{prefix}stage={Stage} {prefix}error={Fault}";
        if (LinuxCgroupFiles.IsControlName(Control)) message += $" {prefix}control={Control}";
        if (CloneFlags is { } flags)
            message += $" {prefix}operation=clone {prefix}flags={FormatCloneFlags(flags)}";
        if (MountOperation is { } operation)
            message += $" {prefix}operation={JsonNamingPolicy.SnakeCaseLower.ConvertName(operation.ToString())}";
        if (MountTarget is { } target)
            message += $" {prefix}target={JsonNamingPolicy.SnakeCaseLower.ConvertName(target.ToString())}";
        if (Errno is > 0 and <= 4095)
        {
            message += FormattableString.Invariant($" {prefix}errno={Errno}");
            if ((CloneFlags.HasValue || NamespaceDetail.HasValue || MountOperation.HasValue) &&
                Enum.IsDefined((NsJailErrno)Errno))
                message += $" {prefix}errno_name={(NsJailErrno)Errno}";
        }
        if (NamespaceDetail is { } detail) message += $" {prefix}detail={detail}";
        if (StderrStatus is { } stderr) message += $" {prefix}stderr={stderr}";
        return message;
    }

    private static string FormatCloneFlags(NsJailNamespaceFlags flags)
    {
        var known = flags & KnownFlags;
        var names = known.ToString().Replace(", ", "|", StringComparison.Ordinal);
        var other = (ulong)(flags & ~KnownFlags);
        if (other == 0) return names;
        var hex = "0x" + other.ToString("x", CultureInfo.InvariantCulture);
        return known == NsJailNamespaceFlags.None ? hex : names + "|" + hex;
    }
}

internal sealed record SandboxFailure(SandboxDiagnostic Cause, SandboxDiagnostic? Cleanup = null)
{
    public string ToLogLine() => "SystemError " + Cause.Format() +
        (Cleanup is { } cleanup ? " " + cleanup.Format("cleanup_") : "");

    public SandboxFailure WithCleanup(SandboxDiagnostic cleanup) =>
        Cleanup is null ? this with { Cleanup = cleanup } : this;

    public static SandboxFailure From(SandboxStage stage, Exception error)
    {
        if (error is SandboxUnavailableException unavailable) return unavailable.Failure;
        SandboxDiagnostic diagnostic = error switch
        {
            Win32Exception native => new(stage, SandboxFault.Native, native.NativeErrorCode),
            UnauthorizedAccessException => new(stage, SandboxFault.AccessDenied),
            IOException => new(stage, SandboxFault.Io),
            TimeoutException => new(stage, SandboxFault.Timeout),
            JsonException => new(stage, SandboxFault.InvalidConfiguration),
            _ => new(stage, SandboxFault.ProcessState)
        };
        return new(diagnostic);
    }

    public static bool IsInfrastructureError(Exception error) => error is SandboxUnavailableException or IOException
        or UnauthorizedAccessException or Win32Exception or InvalidOperationException or NotSupportedException
        or TimeoutException or JsonException or TypeInitializationException or EntryPointNotFoundException
        or DllNotFoundException;
}

internal sealed class SandboxUnavailableException : Exception
{
    public SandboxFailure Failure { get; }

    public SandboxUnavailableException(SandboxStage stage = SandboxStage.Configuration,
        SandboxFault fault = SandboxFault.Rejected, int errno = 0, string? control = null)
        : this(new SandboxFailure(new(stage, fault, errno, control))) { }

    public SandboxUnavailableException(SandboxFailure failure)
        : base("Required sandbox isolation or cleanup could not be verified.") => Failure = failure;
}

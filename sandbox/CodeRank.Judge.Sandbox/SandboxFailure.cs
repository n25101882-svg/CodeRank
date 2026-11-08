using System.ComponentModel;
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

[Flags]
internal enum NsJailNamespaceFlags
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

internal enum NsJailCloneErrno
{
    Unknown = 0, EPERM = 1, E2BIG = 7, EAGAIN = 11, ENOMEM = 12, EACCES = 13, EBUSY = 16,
    EEXIST = 17, EINVAL = 22, ENOSPC = 28, ENOSYS = 38, EUSERS = 87, EOPNOTSUPP = 95
}

internal readonly record struct SandboxDiagnostic(SandboxStage Stage, SandboxFault Fault = SandboxFault.Rejected,
    int Errno = 0, string? Control = null, NsJailNamespaceFlags? CloneFlags = null)
{
    public string Format(string prefix = "")
    {
        var message = $"{prefix}stage={Stage} {prefix}error={Fault}";
        if (LinuxCgroupFiles.IsControlName(Control)) message += $" {prefix}control={Control}";
        if (CloneFlags is { } flags)
            message += $" {prefix}operation=clone {prefix}flags={flags.ToString().Replace(", ", "|", StringComparison.Ordinal)}";
        if (Errno is > 0 and <= 4095)
        {
            message += FormattableString.Invariant($" {prefix}errno={Errno}");
            if (CloneFlags.HasValue && Enum.IsDefined((NsJailCloneErrno)Errno))
                message += $" {prefix}errno_name={(NsJailCloneErrno)Errno}";
        }
        return message;
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

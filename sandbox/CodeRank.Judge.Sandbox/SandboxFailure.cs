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

internal readonly record struct SandboxDiagnostic(SandboxStage Stage, SandboxFault Fault = SandboxFault.Rejected,
    int Errno = 0, string? Control = null)
{
    public string Format(string prefix = "") =>
        $"{prefix}stage={Stage} {prefix}error={Fault}" +
        (LinuxCgroupFiles.IsControlName(Control) ? $" {prefix}control={Control}" : "") +
        (Errno is > 0 and <= 4095 ? FormattableString.Invariant($" {prefix}errno={Errno}") : "");
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

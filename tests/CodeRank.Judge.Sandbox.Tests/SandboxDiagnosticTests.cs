using System.ComponentModel;
using System.Text;
using CodeRank.Judge.Sandbox;
using Xunit;

namespace CodeRank.Judge.Sandbox.Tests;

public sealed class SandboxDiagnosticTests
{
    private const string CloneWarningHeader = "[W][2026-11-08T12:17:12+0000][22358] runChild():477 ";
    private const string NamespaceFlags = "CLONE_NEWNS|CLONE_NEWCGROUP|CLONE_NEWUTS|CLONE_NEWIPC|CLONE_NEWUSER|CLONE_NEWPID|CLONE_NEWNET";

    [Theory]
    [InlineData("FL:2", "LauncherBootstrap", 2)]
    [InlineData("FC:13", "LauncherCgroup", 13)]
    [InlineData("FA:0", "JailAttestation", 0)]
    [InlineData("FM:13", "JailMounts", 13)]
    [InlineData("FS:1", "JailSeccomp", 1)]
    [InlineData("RFE:4095", "JailProgramStart", 4095)]
    [InlineData("RF", "JailProgramStart", 0)]
    [InlineData(null, "LauncherBootstrap", 0)]
    [InlineData("RFE:4096", "Receipt", 0)]
    [InlineData("FA:13\n", "Receipt", 0)]
    [InlineData("RFL:1", "Receipt", 0)]
    [InlineData("RFE:-1", "Receipt", 0)]
    [InlineData("untrusted diagnostic", "Receipt", 0)]
    public void Native_receipt_only_accepts_fixed_stages_and_bounded_errno(string? receipt, string stage, int errno)
    {
        var failure = Assert.IsType<SandboxFailure>(SandboxReceipt.Explain(receipt, "private marker"u8, false));
        Assert.Equal(Enum.Parse<SandboxStage>(stage), failure.Cause.Stage);
        Assert.Equal(errno, failure.Cause.Errno);
        Assert.DoesNotContain("private marker", failure.ToLogLine());
        if (errno == 0) Assert.DoesNotContain("errno=", failure.ToLogLine());
    }

    [Theory]
    [InlineData("clone(flags=CLONE_NEWUSER) failed", "NsJailNamespace")]
    [InlineData("Failed to build mount tree", "NsJailMount")]
    [InlineData("Couldn't prepare sandboxing policy", "NsJailSeccomp")]
    [InlineData("seccomp(SECCOMP_SET_MODE_FILTER", "NsJailSeccomp")]
    [InlineData("unknown setup failure", "NsJailSetup")]
    public void NsJail_startup_diagnostics_are_reduced_to_a_stage(string warning, string stage)
    {
        var stderr = Encoding.UTF8.GetBytes(warning + " /private/source-canary");
        var failure = Assert.IsType<SandboxFailure>(SandboxReceipt.Explain("", stderr, false));
        Assert.Equal(Enum.Parse<SandboxStage>(stage), failure.Cause.Stage);
        Assert.DoesNotContain("source-canary", failure.ToLogLine());
        Assert.DoesNotContain(warning, failure.ToLogLine());
        Assert.Null(SandboxReceipt.Explain("R", stderr, false));
    }

    [Theory]
    [InlineData("Operation not permitted", 1, "EPERM")]
    [InlineData("Argument list too long", 7, "E2BIG")]
    [InlineData("Resource temporarily unavailable", 11, "EAGAIN")]
    [InlineData("Cannot allocate memory", 12, "ENOMEM")]
    [InlineData("Permission denied", 13, "EACCES")]
    [InlineData("Device or resource busy", 16, "EBUSY")]
    [InlineData("File exists", 17, "EEXIST")]
    [InlineData("Invalid argument", 22, "EINVAL")]
    [InlineData("No space left on device", 28, "ENOSPC")]
    [InlineData("Function not implemented", 38, "ENOSYS")]
    [InlineData("Too many users", 87, "EUSERS")]
    [InlineData("Operation not supported", 95, "EOPNOTSUPP")]
    public void Verified_ns_jail_clone_warning_reports_flags_and_linux_errno(string reason, int errno, string name)
    {
        var stderr = Encoding.UTF8.GetBytes("unrelated /private/canary\r\n" +
            CloneWarningHeader + $"clone(flags={NamespaceFlags}) failed: {reason}\r\n");
        var failure = Assert.IsType<SandboxFailure>(SandboxReceipt.Explain("", stderr, false));
        Assert.Equal($"SystemError stage=NsJailNamespace error=Native operation=clone flags={NamespaceFlags} " +
            $"errno={errno} errno_name={name}", failure.ToLogLine());
    }

    [Theory]
    [InlineData("CLONE_NEWTIME|CLONE_NEWUSER", "CLONE_NEWTIME|CLONE_NEWUSER")]
    [InlineData("", "None")]
    public void Clone_warning_reports_the_actual_namespace_subset(string flags, string expected)
    {
        var stderr = Encoding.UTF8.GetBytes(CloneWarningHeader + $"clone(flags={flags}) failed: Invalid argument");
        var failure = Assert.IsType<SandboxFailure>(SandboxReceipt.Explain("", stderr, false));
        Assert.Equal($"SystemError stage=NsJailNamespace error=Native operation=clone flags={expected} errno=22 errno_name=EINVAL",
            failure.ToLogLine());
    }

    [Fact]
    public void Unknown_strerror_keeps_verified_flags_without_guessing_an_errno_or_echoing_text()
    {
        var stderr = Encoding.UTF8.GetBytes(CloneWarningHeader +
            "clone(flags=CLONE_NEWUSER) failed: Operation not permitted /private/canary");
        var failure = Assert.IsType<SandboxFailure>(SandboxReceipt.Explain("", stderr, false));
        Assert.Equal("SystemError stage=NsJailNamespace error=Rejected operation=clone flags=CLONE_NEWUSER", failure.ToLogLine());
    }

    [Theory]
    [InlineData("clone(flags=CLONE_NEWUSER) failed: Operation not permitted")]
    [InlineData(CloneWarningHeader + "clone(flags=CLONE_VM) failed: Operation not permitted")]
    [InlineData(CloneWarningHeader + "clone(flags=CLONE_NEWUSER|0x1234) failed: Operation not permitted")]
    [InlineData(CloneWarningHeader + "clone(flags=CLONE_NEWUSER|CLONE_NEWUSER) failed: Operation not permitted")]
    [InlineData(CloneWarningHeader + "clone(flags=CLONE_NEWUSER||CLONE_NEWNET) failed: Operation not permitted")]
    [InlineData(CloneWarningHeader + "clone(flags=CLONE_NEWUSER) failed")]
    [InlineData("[W][2026-11-08T12:17:12+0000][22358] initParent():477 clone(flags=CLONE_NEWUSER) failed: Operation not permitted")]
    public void Unrecognized_clone_records_keep_the_generic_namespace_diagnostic(string stderr)
    {
        var failure = Assert.IsType<SandboxFailure>(SandboxReceipt.Explain("", Encoding.UTF8.GetBytes(stderr), false));
        Assert.Equal("SystemError stage=NsJailNamespace error=Rejected", failure.ToLogLine());
    }

    [Fact]
    public void Clone_warning_cannot_override_a_ready_or_native_failure_receipt()
    {
        var stderr = Encoding.UTF8.GetBytes(CloneWarningHeader + "clone(flags=CLONE_NEWUSER) failed: Operation not permitted");
        Assert.Null(SandboxReceipt.Explain("R", stderr, false));
        var failure = Assert.IsType<SandboxFailure>(SandboxReceipt.Explain("FA:13", stderr, false));
        Assert.Equal("SystemError stage=JailAttestation error=Native errno=13", failure.ToLogLine());
    }

    [Fact]
    public void First_failure_stays_visible_when_cleanup_also_fails()
    {
        var error = new SandboxUnavailableException(SandboxStage.CgroupSetup, SandboxFault.Native, 13, "memory.max");
        var failure = SandboxFailure.From(SandboxStage.Runner, error)
            .WithCleanup(SandboxFailure.From(SandboxStage.ScratchCleanup, new IOException("source-canary")).Cause);
        Assert.Equal("SystemError stage=CgroupSetup error=Native control=memory.max errno=13 " +
            "cleanup_stage=ScratchCleanup cleanup_error=Io", failure.ToLogLine());
    }

    [Fact]
    public void Diagnostics_do_not_format_exception_messages_or_unknown_control_names()
    {
        var failure = SandboxFailure.From(SandboxStage.LauncherStart, new Win32Exception(13, "lease-canary"));
        Assert.Equal("SystemError stage=LauncherStart error=Native errno=13", failure.ToLogLine());
        Assert.False(LinuxCgroupFiles.IsControlName("../private-test"));
        var invalid = new SandboxFailure(new(SandboxStage.CgroupSetup, SandboxFault.Native, 50_000, "../private-test"));
        Assert.Equal("SystemError stage=CgroupSetup error=Native", invalid.ToLogLine());
    }
}

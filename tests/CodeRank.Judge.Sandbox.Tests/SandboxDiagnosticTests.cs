using System.ComponentModel;
using System.Text;
using CodeRank.Judge.Sandbox;
using Xunit;

namespace CodeRank.Judge.Sandbox.Tests;

public sealed class SandboxDiagnosticTests
{
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

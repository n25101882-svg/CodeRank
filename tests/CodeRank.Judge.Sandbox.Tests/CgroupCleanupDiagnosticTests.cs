using System.Diagnostics;
using CodeRank.Judge.Sandbox;
using Xunit;

namespace CodeRank.Judge.Sandbox.Tests;

public sealed class CgroupCleanupDiagnosticTests
{
    private static readonly TimeSpan CleanupDeadline = TimeSpan.FromSeconds(3);

    [Fact]
    public void A_populated_group_after_a_delayed_poll_still_fails_closed()
    {
        var drain = new CgroupDrainObservation(Timestamp(10_000), CleanupDeadline);
        Assert.False(drain.Observe(true, Timestamp(10_025)));
        Assert.True(drain.Observe(true, Timestamp(13_250)));

        Assert.Equal(2, drain.Polls);
        Assert.Equal(Timestamp(13_250), drain.LastSample);
        Assert.Equal(Timestamp(3_225), drain.MaxPollGap);
        var failure = Assert.Throws<SandboxUnavailableException>(() => LinuxJobCgroup.RequireDrained(drain.Populated, null));
        Assert.Equal(new SandboxDiagnostic(SandboxStage.CgroupCleanup, SandboxFault.Timeout, Control: "cgroup.events"),
            failure.Failure.Cause);
    }

    [Theory]
    [InlineData(2_950)]
    [InlineData(3_250)]
    public void An_empty_sample_confirms_drain_even_if_the_poll_was_delayed(int milliseconds)
    {
        var drain = new CgroupDrainObservation(Timestamp(10_000), CleanupDeadline);
        Assert.False(drain.Observe(true, Timestamp(10_025)));
        Assert.True(drain.Observe(false, Timestamp(10_000 + milliseconds)));
        LinuxJobCgroup.RequireDrained(drain.Populated, null);
    }

    [Fact]
    public void An_empty_group_does_not_replace_confirmation_of_launcher_exit()
    {
        var launcherFailure = new SandboxFailure(new(SandboxStage.ProcessCleanup, SandboxFault.Timeout));
        var failure = Assert.Throws<SandboxUnavailableException>(() => LinuxJobCgroup.RequireDrained(false, launcherFailure));
        Assert.Same(launcherFailure, failure.Failure);
    }

    [Fact]
    public void Missing_population_evidence_fails_closed()
    {
        var failure = Assert.Throws<SandboxUnavailableException>(() => LinuxJobCgroup.RequireDrained(null, null));
        Assert.Equal(SandboxFault.InvalidData, failure.Failure.Cause.Fault);
    }

    [Fact]
    public void Cleanup_failure_retains_bounded_timing_fields_when_the_invocation_already_failed()
    {
        var drain = new CgroupDrainObservation(Timestamp(12_002), CleanupDeadline);
        Assert.True(drain.Observe(true, Timestamp(15_400)));
        var observation = new CgroupCleanupDiagnostic(123, SandboxFault.Timeout, Timestamp(9_000), Timestamp(12_000),
            Timestamp(12_002), drain, new(Timestamp(4_000), TimeSpan.FromSeconds(2), SandboxStopReason.WallDeadline));
        var failure = new SandboxFailure(new(SandboxStage.ProcessIo, SandboxFault.Io)).WithCleanup(
            new SandboxDiagnostic(SandboxStage.CgroupCleanup, SandboxFault.Timeout, Control: "cgroup.events")
                { CgroupCleanup = observation });

        Assert.Equal("SystemError stage=ProcessIo error=Io cleanup_stage=CgroupCleanup cleanup_error=Timeout " +
            "cleanup_control=cgroup.events cleanup_stop_reason=WallDeadline cleanup_wall_due_ms=6000 " +
            "cleanup_stop_ms=9000 cleanup_leader_pid=123 cleanup_leader_exit=unconfirmed cleanup_kill_begin_ms=12000 " +
            "cleanup_leader_error=Timeout cleanup_kill_end_ms=12002 cleanup_polls=1 cleanup_populated=1 " +
            "cleanup_sample_ms=15400 cleanup_max_poll_gap_ms=3398", failure.ToLogLine());
    }

    [Fact]
    public void Failed_kill_does_not_report_a_successful_write_or_population_observation()
    {
        var observation = new CgroupCleanupDiagnostic(null, null, Timestamp(1_000), Timestamp(1_002), null, null,
            new(Timestamp(900), TimeSpan.FromSeconds(2), SandboxStopReason.Cancelled));
        var line = new SandboxFailure(new SandboxDiagnostic(SandboxStage.CgroupCleanup, SandboxFault.Native,
            Errno: 13, Control: "cgroup.kill") { CgroupCleanup = observation }).ToLogLine();

        Assert.Contains("leader_pid=none leader_exit=not_started", line);
        Assert.Contains("polls=0 populated=unknown", line);
        Assert.DoesNotContain("kill_end_ms", line);
        Assert.DoesNotContain("sample_ms", line);
    }

    private static long Timestamp(int milliseconds) => milliseconds * Stopwatch.Frequency / 1_000;
}

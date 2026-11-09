using System.Diagnostics;
using System.Globalization;

namespace CodeRank.Judge.Sandbox;

internal enum SandboxStopReason { ProcessIo, WallDeadline, Cancelled, OutputLimit }

internal readonly record struct SandboxCleanupContext(long InvocationStartedAt, TimeSpan WallBudget, SandboxStopReason Reason);

internal sealed class CgroupDrainObservation(long startedAt, TimeSpan timeout)
{
    public int Polls { get; private set; }
    public bool? Populated { get; private set; }
    public long? LastSample { get; private set; }
    public long MaxPollGap { get; private set; }

    public bool Observe(bool populated, long timestamp)
    {
        MaxPollGap = Math.Max(MaxPollGap, timestamp - (LastSample ?? startedAt));
        LastSample = timestamp;
        Populated = populated;
        Polls++;
        return !populated || Stopwatch.GetElapsedTime(startedAt, timestamp) >= timeout;
    }
}

internal sealed record CgroupCleanupDiagnostic(int? LeaderPid, SandboxFault? LeaderFailure,
    long StartedAt, long KillStartedAt, long? KillReturnedAt, CgroupDrainObservation? Drain, SandboxCleanupContext Context)
{
    public string Format(string prefix)
    {
        var leaderExit = LeaderPid is null ? "not_started" : LeaderFailure is null ? "confirmed" : "unconfirmed";
        var populated = Drain?.Populated switch { true => "1", false => "0", _ => "unknown" };
        var wallDue = Milliseconds(Context.InvocationStartedAt) + Context.WallBudget.Ticks / TimeSpan.TicksPerMillisecond;
        var message = FormattableString.Invariant($" {prefix}stop_reason={Context.Reason} {prefix}wall_due_ms={wallDue} {prefix}stop_ms={Milliseconds(StartedAt)}");
        message += FormattableString.Invariant($" {prefix}leader_pid={LeaderPid?.ToString(CultureInfo.InvariantCulture) ?? "none"} {prefix}leader_exit={leaderExit} {prefix}kill_begin_ms={Milliseconds(KillStartedAt)}");
        if (LeaderFailure is { } leaderFailure) message += $" {prefix}leader_error={leaderFailure}";
        if (KillReturnedAt is { } killReturned)
            message += FormattableString.Invariant($" {prefix}kill_end_ms={Milliseconds(killReturned)}");
        message += FormattableString.Invariant($" {prefix}polls={Drain?.Polls ?? 0} {prefix}populated={populated}");
        if (Drain?.LastSample is { } lastSample)
            message += FormattableString.Invariant(
                $" {prefix}sample_ms={Milliseconds(lastSample)} {prefix}max_poll_gap_ms={Milliseconds(Drain.MaxPollGap)}");
        return message;
    }

    private static long Milliseconds(long ticks) => Stopwatch.GetElapsedTime(0, ticks).Ticks / TimeSpan.TicksPerMillisecond;
}

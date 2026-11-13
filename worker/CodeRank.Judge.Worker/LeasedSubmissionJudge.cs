using System.Net;
using System.Text.Json;
using CodeRank.Judge.Protocol;

namespace CodeRank.Judge.Worker;

internal enum LeasedJudgeResult { Finished, LeaseEnded, Halt }

internal sealed class LeasedSubmissionJudge(JudgeApiClient api, SubmissionGrader grader, WorkerSettings settings,
    TimeProvider clock, ILogger<LeasedSubmissionJudge> logger)
{
    private enum LeaseState { Confirmed, Lost, Unconfirmed }

    // An accepted heartbeat grants at least this window from the request's start, regardless of UTC clock skew.
    private static readonly TimeSpan ConfirmedWindow = TimeSpan.FromSeconds(JudgePayloadContract.LeaseSeconds - 5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    public async Task<LeasedJudgeResult> RunAsync(JobLease job, JudgePayload payload, CancellationToken stoppingToken)
    {
        using var lease = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        await using var expiry = clock.CreateTimer(static state => ((CancellationTokenSource)state!).Cancel(), lease,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        var initial = await ConfirmLeaseAsync(job, lease, expiry);
        if (initial != LeaseState.Confirmed) return StopWithoutVerdict(initial);

        using var stopRenewing = new CancellationTokenSource();
        var renewals = MaintainLeaseAsync(job, lease, expiry, stopRenewing.Token);
        string? verdict = null;
        LeaseState renewal;
        try { verdict = await grader.GradeAsync(payload, lease.Token); }
        catch (OperationCanceledException) when (lease.IsCancellationRequested) { }
        finally
        {
            await stopRenewing.CancelAsync();
            // Observe any in-flight heartbeat before deciding whether it is safe to post a verdict.
            renewal = await renewals;
        }

        var infrastructureFailure = verdict == SubmissionGrader.SystemError;
        if (stoppingToken.IsCancellationRequested) return LeasedJudgeResult.LeaseEnded;
        if (verdict is null || lease.IsCancellationRequested)
            return infrastructureFailure ? LeasedJudgeResult.Halt : StopWithoutVerdict(renewal);
        var final = await ConfirmLeaseAsync(job, lease, expiry);
        if (final != LeaseState.Confirmed)
            return infrastructureFailure ? LeasedJudgeResult.Halt : StopWithoutVerdict(final);

        try
        {
            var result = await TryTwiceAsync(token => api.CompleteAsync(job, verdict, token), lease.Token);
            logger.LogInformation("Completion for submission {SubmissionId}: {CompletionState}.", job.Id, result);
            if (infrastructureFailure || result is CompleteResult.Disabled or CompleteResult.VerdictConflict)
                return LeasedJudgeResult.Halt;
            return result == CompleteResult.Saved ? LeasedJudgeResult.Finished : LeasedJudgeResult.LeaseEnded;
        }
        catch (Exception error) when (IsApiFailure(error))
        {
            // The server may have saved the result. Stop polling instead of repeating execution.
            logger.LogWarning("Completion for submission {SubmissionId} was not confirmed.", job.Id);
            return LeasedJudgeResult.Halt;
        }
    }

    private async Task<LeaseState> MaintainLeaseAsync(JobLease job, CancellationTokenSource lease, ITimer expiry,
        CancellationToken stopRenewing)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(stopRenewing, lease.Token);
        try
        {
            while (!wait.IsCancellationRequested)
            {
                await Task.Delay(settings.HeartbeatInterval, clock, wait.Token);
                if (wait.IsCancellationRequested) break;
                var renewal = await ConfirmLeaseAsync(job, lease, expiry);
                if (renewal != LeaseState.Confirmed) return renewal;
            }
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested) { }
        return lease.IsCancellationRequested ? LeaseState.Unconfirmed : LeaseState.Confirmed;
    }

    private async Task<LeaseState> ConfirmLeaseAsync(JobLease job, CancellationTokenSource lease, ITimer expiry)
    {
        var state = LeaseState.Unconfirmed;
        try
        {
            state = await TryTwiceAsync(async token =>
            {
                var sent = clock.GetTimestamp();
                if (!await api.HeartbeatAsync(job, token)) return LeaseState.Lost;
                var remaining = ConfirmedWindow - clock.GetElapsedTime(sent);
                return remaining > TimeSpan.Zero && !lease.IsCancellationRequested &&
                    expiry.Change(remaining, Timeout.InfiniteTimeSpan) ? LeaseState.Confirmed : LeaseState.Unconfirmed;
            }, lease.Token);
            if (state == LeaseState.Confirmed) return state;
        }
        catch (Exception error) when (IsApiFailure(error)) { }

        if (!lease.IsCancellationRequested)
            logger.LogWarning("Lease for submission {SubmissionId} could not be confirmed.", job.Id);
        await lease.CancelAsync();
        return state;
    }

    private static LeasedJudgeResult StopWithoutVerdict(LeaseState state) =>
        state == LeaseState.Lost ? LeasedJudgeResult.LeaseEnded : LeasedJudgeResult.Halt;

    private async Task<T> TryTwiceAsync<T>(Func<CancellationToken, Task<T>> send, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { return await send(token); }
        catch (Exception error) when (!token.IsCancellationRequested && IsTransient(error))
        {
            await Task.Delay(RetryDelay, clock, token);
            token.ThrowIfCancellationRequested();
            return await send(token);
        }
    }

    private static bool IsApiFailure(Exception error) => error is HttpRequestException or JsonException or OperationCanceledException;

    private static bool IsTransient(Exception error) => error is OperationCanceledException ||
        error is HttpRequestException request &&
        (request.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
            (int)request.StatusCode >= 500);
}

using System.Text.Json;

namespace CodeRank.Judge.Worker;

internal sealed class JudgeWorker(JudgeApiClient api, WorkerSettings settings, ILogger<JudgeWorker> logger,
    LeasedSubmissionJudge? evaluator = null, IHostApplicationLifetime? lifetime = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        JobLease? job = null;
        var payloadRead = false;
        var renewLease = true;
        var retryDelay = settings.InitialRetryDelay;
        logger.LogInformation("Judge worker {WorkerId} started.", settings.WorkerId);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = settings.IdleInterval;
                try
                {
                    if (job is null)
                    {
                        payloadRead = false;
                        renewLease = true;
                        job = await api.ClaimAsync(stoppingToken);
                        if (job is not null) logger.LogInformation("Reserved submission {SubmissionId}.", job.Id);
                    }
                    else if (renewLease && !await api.HeartbeatAsync(job, stoppingToken))
                    {
                        logger.LogInformation("Lease for submission {SubmissionId} ended.", job.Id);
                        job = null;
                    }

                    if (job is not null && settings.FetchPayload && !payloadRead)
                    {
                        var read = await api.GetPayloadAsync(job, stoppingToken);
                        logger.LogInformation("Payload state for submission {SubmissionId}: {PayloadState}.", job.Id, read.State);
                        switch (read.State)
                        {
                            case PayloadReadState.Ready when settings.EvaluateSubmissions:
                                if (evaluator is null || await evaluator.RunAsync(job, read.Payload!, stoppingToken) == LeasedJudgeResult.Halt)
                                {
                                    logger.LogError("Judge evaluation halted. Check the sandbox and completion configuration.");
                                    lifetime?.StopApplication();
                                    return;
                                }
                                job = null;
                                break;
                            case PayloadReadState.Ready:
                                payloadRead = true;
                                renewLease = true;
                                break;
                            case PayloadReadState.LeaseLost:
                                job = null;
                                break;
                            case PayloadReadState.TestsUnavailable:
                            case PayloadReadState.InvalidPayload:
                                // Let the current lease expire if the failure cannot be confirmed.
                                renewLease = false;
                                var failure = await api.FailPayloadAsync(job, read.State, stoppingToken);
                                logger.LogInformation("Payload failure report for submission {SubmissionId}: {Result}.", job.Id, failure);
                                if (failure != PayloadFailureResult.NotConfirmed) job = null;
                                break;
                        }
                    }

                    if (job is not null) delay = settings.HeartbeatInterval;
                    retryDelay = settings.InitialRetryDelay;
                }
                catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    logger.LogWarning("Judge API request failed ({FailureType}, HTTP {Status}). Retrying.",
                        error.GetType().Name, (int?)(error as HttpRequestException)?.StatusCode);
                    delay = retryDelay;
                    retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, settings.MaximumRetryDelay.Ticks));
                }

                await Task.Delay(delay, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        logger.LogInformation("Judge worker stopped.");
    }
}

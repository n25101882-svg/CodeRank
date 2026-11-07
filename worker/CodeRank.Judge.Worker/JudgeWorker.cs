using System.Text.Json;

namespace CodeRank.Judge.Worker;

internal sealed class JudgeWorker(JudgeApiClient api, WorkerSettings settings, ILogger<JudgeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        JobLease? job = null;
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
                        job = await api.ClaimAsync(stoppingToken);
                        if (job is not null) logger.LogInformation("Reserved submission {SubmissionId}.", job.Id);
                    }
                    else if (!await api.HeartbeatAsync(job, stoppingToken))
                    {
                        logger.LogInformation("Lease for submission {SubmissionId} ended.", job.Id);
                        job = null;
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

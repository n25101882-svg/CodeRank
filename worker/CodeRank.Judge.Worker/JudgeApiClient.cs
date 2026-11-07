using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CodeRank.Judge.Protocol;

namespace CodeRank.Judge.Worker;

internal sealed record JobLease(long Id, long ProblemId, string Language, Guid LeaseToken, DateTimeOffset LeaseExpiresAtUtc);
internal sealed record LeaseHeartbeat(DateTimeOffset LeaseExpiresAtUtc);

internal sealed class JudgeApiClient(HttpClient http, WorkerSettings settings)
{
    public async Task<JobLease?> ClaimAsync(CancellationToken token)
    {
        using var response = await PostAsync("claim", null, token);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        RequireOk(response);
        var job = await response.Content.ReadFromJsonAsync<JobLease>(token);
        if (job is null || job.Id <= 0 || job.ProblemId <= 0 || job.Language is not ("cpp17" or "python3") ||
            job.LeaseToken == Guid.Empty || !IsUtc(job.LeaseExpiresAtUtc))
            throw new JsonException("Judge API returned an invalid claim.");
        return job;
    }

    public async Task<bool> HeartbeatAsync(JobLease job, CancellationToken token)
    {
        using var response = await PostAsync(FormattableString.Invariant($"{job.Id}/heartbeat"),
            JsonContent.Create(new { leaseToken = job.LeaseToken }), token);
        if (response.StatusCode == HttpStatusCode.Conflict) return false;
        RequireOk(response);
        var heartbeat = await response.Content.ReadFromJsonAsync<LeaseHeartbeat>(token);
        if (heartbeat is null || !IsUtc(heartbeat.LeaseExpiresAtUtc))
            throw new JsonException("Judge API returned an invalid heartbeat.");
        return true;
    }

    private async Task<HttpResponseMessage> PostAsync(string path, HttpContent? body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.BaseAddress, "api/v1/judge/jobs/" + path))
        {
            Content = body
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(WorkerCredentials.Scheme, settings.Key);
        request.Headers.Add(WorkerCredentials.WorkerIdHeader, settings.WorkerId);
        return await http.SendAsync(request, token);
    }

    private static void RequireOk(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException("Judge API request failed.", null, response.StatusCode);
    }

    private static bool IsUtc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
}

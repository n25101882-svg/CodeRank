using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CodeRank.Judge.Protocol;

namespace CodeRank.Judge.Worker;

internal sealed record JobLease(long Id, long ProblemId, string Language, Guid LeaseToken, DateTimeOffset LeaseExpiresAtUtc);
internal sealed record LeaseHeartbeat(DateTimeOffset LeaseExpiresAtUtc);
internal enum PayloadReadState { Ready, LeaseLost, TestsUnavailable, InvalidPayload }
internal sealed record PayloadReadResult(PayloadReadState State, JudgePayload? Payload = null);
internal enum PayloadFailureResult { Finished, LeaseLost, NotConfirmed }

internal sealed class JudgeApiClient(HttpClient http, WorkerSettings settings)
{
    private const int MaxProblemBytes = 8 * 1024;

    private sealed class PayloadProblem
    {
        public string? Code { get; init; }
    }

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

    public async Task<PayloadReadResult> GetPayloadAsync(JobLease job, CancellationToken token)
    {
        using var request = CreateRequest(HttpMethod.Get, FormattableString.Invariant($"{job.Id}/payload"));
        request.Headers.Add(JudgePayloadContract.LeaseHeader, job.LeaseToken.ToString("D"));

        // ResponseHeadersRead keeps the small Stage 1 buffer limit. Bound the larger body and its read deadline here.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(http.Timeout);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
        {
            var problem = await ReadPayloadJsonAsync<PayloadProblem>(response.Content, MaxProblemBytes, deadline.Token);
            return (response.StatusCode, problem?.Code) switch
            {
                (HttpStatusCode.Conflict, JudgePayloadContract.LeaseLostCode) => new(PayloadReadState.LeaseLost),
                (HttpStatusCode.Conflict, JudgePayloadContract.TestsUnavailableCode) => new(PayloadReadState.TestsUnavailable),
                (HttpStatusCode.UnprocessableEntity, JudgePayloadContract.InvalidPayloadCode) => new(PayloadReadState.InvalidPayload),
                _ => throw new HttpRequestException("Judge API request failed.", null, response.StatusCode)
            };
        }

        RequireOk(response);
        var payload = await ReadPayloadJsonAsync<JudgePayload>(response.Content, JudgePayloadContract.MaxResponseBytes, deadline.Token);
        if (payload is null || payload.Id != job.Id || payload.ProblemId != job.ProblemId || payload.Language != job.Language ||
            !JudgePayloadContract.IsValid(payload))
            throw new JsonException("Judge API returned an invalid payload.");
        return new(PayloadReadState.Ready, payload);
    }

    public async Task<PayloadFailureResult> FailPayloadAsync(JobLease job, PayloadReadState state, CancellationToken token)
    {
        var reason = state switch
        {
            PayloadReadState.TestsUnavailable => JudgePayloadContract.TestsUnavailableCode,
            PayloadReadState.InvalidPayload => JudgePayloadContract.InvalidPayloadCode,
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
        using var response = await PostAsync(FormattableString.Invariant($"{job.Id}/fail"),
            JsonContent.Create(new { leaseToken = job.LeaseToken, reason }), token);
        if (response.StatusCode == HttpStatusCode.NoContent) return PayloadFailureResult.Finished;
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var problem = await ReadPayloadJsonAsync<PayloadProblem>(response.Content, MaxProblemBytes, token);
            if (problem?.Code == JudgePayloadContract.LeaseLostCode) return PayloadFailureResult.LeaseLost;
            if (problem?.Code == JudgePayloadContract.FailureNotConfirmedCode) return PayloadFailureResult.NotConfirmed;
        }
        throw new HttpRequestException("Judge API request failed.", null, response.StatusCode);
    }

    private static async Task<T?> ReadPayloadJsonAsync<T>(HttpContent content, int maxBytes, CancellationToken token)
    {
        try
        {
            await content.LoadIntoBufferAsync(maxBytes, token);
            using var stream = await content.ReadAsStreamAsync(token);
            return await JsonSerializer.DeserializeAsync<T>(stream, JudgePayloadContract.JsonOptions, token);
        }
        catch (JsonException)
        {
            throw new JsonException("Judge API returned invalid payload JSON.");
        }
    }

    private async Task<HttpResponseMessage> PostAsync(string path, HttpContent? body, CancellationToken token)
    {
        using var request = CreateRequest(HttpMethod.Post, path);
        request.Content = body;
        return await http.SendAsync(request, token);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(settings.BaseAddress, "api/v1/judge/jobs/" + path));
        request.Headers.Authorization = new AuthenticationHeaderValue(WorkerCredentials.Scheme, settings.Key);
        request.Headers.Add(WorkerCredentials.WorkerIdHeader, settings.WorkerId);
        return request;
    }

    private static void RequireOk(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException("Judge API request failed.", null, response.StatusCode);
    }

    private static bool IsUtc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
}

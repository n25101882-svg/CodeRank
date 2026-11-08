using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeRank.Judge.Worker;
using Xunit;

namespace CodeRank.Judge.Worker.Tests;

public sealed partial class JudgeWorkerTests
{
    private const string TestsUnavailable = "judge_tests_unavailable";

    [Theory]
    [InlineData(HttpStatusCode.Conflict, TestsUnavailable)]
    [InlineData(HttpStatusCode.UnprocessableEntity, "judge_payload_invalid")]
    public async Task Permanent_payload_failure_is_reported_and_the_worker_claims_the_next_submission(
        HttpStatusCode status, string reason)
    {
        var settings = PayloadSettings();
        var lease = TestLease();
        var next = lease with { Id = lease.Id + 1, LeaseToken = Guid.NewGuid() };
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var routes = new ConcurrentQueue<string>();
        var logger = new MemoryLogger();
        var calls = 0;
        using var http = new HttpClient(new MockApi(async (request, token) =>
        {
            routes.Enqueue(request.RequestUri!.Segments[^1]);
            switch (Interlocked.Increment(ref calls))
            {
                case 1: return Json(lease);
                case 2: return FailureProblem(status, reason);
                case 3:
                    Assert.Equal(HttpMethod.Post, request.Method);
                    Assert.Equal("/api/v1/judge/jobs/42/fail", request.RequestUri.PathAndQuery);
                    Assert.Equal("CodeRankJudge", request.Headers.Authorization?.Scheme);
                    Assert.True(request.Headers.Authorization?.Parameter == settings.Key, "Wrong worker credential.");
                    Assert.Equal(settings.WorkerId, Assert.Single(request.Headers.GetValues("X-CodeRank-Worker")));
                    using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)))
                    {
                        Assert.Equal(2, body.RootElement.EnumerateObject().Count());
                        Assert.True(body.RootElement.GetProperty("leaseToken").GetGuid() == lease.LeaseToken, "Wrong lease.");
                        Assert.Equal(reason, body.RootElement.GetProperty("reason").GetString());
                    }
                    return new(HttpStatusCode.NoContent);
                case 4: return Json(next);
                case 5:
                    Assert.Equal("/api/v1/judge/jobs/43/payload", request.RequestUri.AbsolutePath);
                    return Json(Payload(next));
                default:
                    Assert.Equal("/api/v1/judge/jobs/43/heartbeat", request.RequestUri.AbsolutePath);
                    finished.TrySetResult();
                    return Json(new LeaseHeartbeat(next.LeaseExpiresAtUtc));
            }
        }));
        using var worker = new JudgeWorker(new JudgeApiClient(http, settings), settings, logger);
        await RunUntilAsync(worker, finished.Task);

        Assert.Equal(new[] { "claim", "payload", "fail", "claim", "payload", "heartbeat" }, routes.Take(6));
        Assert.DoesNotContain("complete", routes);
        Assert.True(logger.Messages.All(message =>
            !message.Contains(settings.Key, StringComparison.Ordinal) &&
            !message.Contains(lease.LeaseToken.ToString(), StringComparison.Ordinal) &&
            !message.Contains("synthetic-test-output", StringComparison.Ordinal)), "Worker logged private data.");
    }

    [Theory]
    [InlineData("disconnected")]
    [InlineData("malformed-success")]
    [InlineData("server-busy")]
    public async Task Transient_payload_errors_do_not_report_failure(string scenario)
    {
        var settings = PayloadSettings();
        var lease = TestLease();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var routes = new ConcurrentQueue<string>();
        var calls = 0;
        using var http = new HttpClient(new MockApi((request, _) =>
        {
            routes.Enqueue(request.RequestUri!.Segments[^1]);
            return Task.FromResult(Interlocked.Increment(ref calls) switch
            {
                1 => Json(lease),
                2 => scenario switch
                {
                    "disconnected" => throw new HttpRequestException("Synthetic connection failure."),
                    "malformed-success" => new(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{", Encoding.UTF8, "application/json")
                    },
                    _ => new(HttpStatusCode.ServiceUnavailable)
                },
                3 => Json(new LeaseHeartbeat(lease.LeaseExpiresAtUtc)),
                4 => Json(Payload(lease)),
                _ => HeartbeatAndSignal()
            });
        }));

        HttpResponseMessage HeartbeatAndSignal()
        {
            finished.TrySetResult();
            return Json(new LeaseHeartbeat(lease.LeaseExpiresAtUtc));
        }

        using var worker = new JudgeWorker(new JudgeApiClient(http, settings), settings, new MemoryLogger());
        await RunUntilAsync(worker, finished.Task);
        Assert.Equal(new[] { "claim", "payload", "heartbeat", "payload", "heartbeat" }, routes.Take(5));
        Assert.DoesNotContain("fail", routes);
        Assert.DoesNotContain("complete", routes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_recovers_a_lost_fail_response_without_extending_the_bad_lease(bool committed)
    {
        var settings = PayloadSettings();
        var lease = TestLease();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var routes = new ConcurrentQueue<string>();
        var calls = 0;
        using var http = new HttpClient(new MockApi((request, _) =>
        {
            routes.Enqueue(request.RequestUri!.Segments[^1]);
            return Task.FromResult(Interlocked.Increment(ref calls) switch
            {
                1 => Json(lease),
                2 => FailureProblem(HttpStatusCode.Conflict, TestsUnavailable),
                3 => throw new HttpRequestException("Synthetic loss of failure response."),
                4 => FailureProblem(HttpStatusCode.Conflict, committed ? "lease_lost" : TestsUnavailable),
                5 when !committed => new(HttpStatusCode.NoContent),
                _ => EmptyQueue()
            });
        }));

        HttpResponseMessage EmptyQueue()
        {
            finished.TrySetResult();
            return new(HttpStatusCode.NoContent);
        }

        using var worker = new JudgeWorker(new JudgeApiClient(http, settings), settings, new MemoryLogger());
        await RunUntilAsync(worker, finished.Task);
        var expected = committed
            ? new[] { "claim", "payload", "fail", "payload", "claim" }
            : new[] { "claim", "payload", "fail", "payload", "fail", "claim" };
        Assert.Equal(expected, routes.Take(expected.Length));
        Assert.DoesNotContain("heartbeat", routes);
    }

    [Fact]
    public async Task Server_rejection_rechecks_payload_before_resuming_heartbeats()
    {
        var settings = PayloadSettings();
        var lease = TestLease();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var routes = new ConcurrentQueue<string>();
        var calls = 0;
        using var http = new HttpClient(new MockApi((request, _) =>
        {
            routes.Enqueue(request.RequestUri!.Segments[^1]);
            return Task.FromResult(Interlocked.Increment(ref calls) switch
            {
                1 => Json(lease),
                2 => FailureProblem(HttpStatusCode.Conflict, TestsUnavailable),
                3 => FailureProblem(HttpStatusCode.Conflict, "judge_payload_failure_not_confirmed"),
                4 => Json(Payload(lease)),
                _ => HeartbeatAndSignal()
            });
        }));

        HttpResponseMessage HeartbeatAndSignal()
        {
            ready.TrySetResult();
            return Json(new LeaseHeartbeat(lease.LeaseExpiresAtUtc));
        }

        using var worker = new JudgeWorker(new JudgeApiClient(http, settings), settings, new MemoryLogger());
        await RunUntilAsync(worker, ready.Task);
        Assert.Equal(new[] { "claim", "payload", "fail", "payload", "heartbeat" }, routes.Take(5));
        Assert.DoesNotContain("complete", routes);
    }

    [Fact]
    public async Task Restarted_worker_can_finish_a_bad_lease_that_was_not_committed()
    {
        var settings = PayloadSettings();
        var lease = TestLease();
        var firstFailureRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextClaim = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var routes = new ConcurrentQueue<string>();
        var calls = 0;
        using var http = new HttpClient(new MockApi(async (request, token) =>
        {
            routes.Enqueue(request.RequestUri!.Segments[^1]);
            switch (Interlocked.Increment(ref calls))
            {
                case 1 or 4: return Json(lease);
                case 2 or 5: return FailureProblem(HttpStatusCode.Conflict, TestsUnavailable);
                case 3:
                    firstFailureRequest.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                    throw new InvalidOperationException("The stopped Worker did not cancel its request.");
                case 6: return new(HttpStatusCode.NoContent);
                default:
                    nextClaim.TrySetResult();
                    return new(HttpStatusCode.NoContent);
            }
        }));
        using (var first = new JudgeWorker(new JudgeApiClient(http, settings), settings, new MemoryLogger()))
            await RunUntilAsync(first, firstFailureRequest.Task);
        using var restarted = new JudgeWorker(new JudgeApiClient(http, settings), settings, new MemoryLogger());
        await RunUntilAsync(restarted, nextClaim.Task);
        Assert.Equal(new[] { "claim", "payload", "fail", "claim", "payload", "fail", "claim" }, routes.Take(7));
        Assert.DoesNotContain("heartbeat", routes);
    }

    [Fact]
    public async Task Worker_claims_again_if_the_lease_ends_before_the_failure_is_accepted()
    {
        var settings = PayloadSettings();
        var lease = TestLease();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var routes = new ConcurrentQueue<string>();
        var calls = 0;
        using var http = new HttpClient(new MockApi((request, _) =>
        {
            routes.Enqueue(request.RequestUri!.Segments[^1]);
            switch (Interlocked.Increment(ref calls))
            {
                case 1: return Task.FromResult(Json(lease));
                case 2: return Task.FromResult(FailureProblem(HttpStatusCode.Conflict, TestsUnavailable));
                case 3: return Task.FromResult(FailureProblem(HttpStatusCode.Conflict, "lease_lost"));
                default:
                    finished.TrySetResult();
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
        }));
        using var worker = new JudgeWorker(new JudgeApiClient(http, settings), settings, new MemoryLogger());
        await RunUntilAsync(worker, finished.Task);
        Assert.Equal(new[] { "claim", "payload", "fail", "claim" }, routes.Take(4));
        Assert.DoesNotContain("heartbeat", routes);
    }

    private static HttpResponseMessage FailureProblem(HttpStatusCode status, string code) =>
        new(status) { Content = JsonContent.Create(new { code }) };
}

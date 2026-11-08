using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeRank.Judge.Protocol;
using CodeRank.Judge.Worker;
using Xunit;

namespace CodeRank.Judge.Worker.Tests;

public sealed partial class JudgeWorkerTests
{
    [Fact]
    public async Task Typed_client_reads_payload_larger_than_the_stage_one_buffer_without_putting_tokens_in_the_url()
    {
        var settings = PayloadSettings();
        var lease = TestLease();
        var expected = Payload(lease, tests:
            [PrivateCase(1, new string('x', JudgePayloadContract.MaxTestFieldBytes), "synthetic-output")]);
        using var http = new HttpClient(new MockApi((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.example.test/api/v1/judge/jobs/42/payload", request.RequestUri!.AbsoluteUri);
            Assert.True(request.Headers.Authorization?.Parameter == settings.Key, "Wrong machine key.");
            Assert.Equal("judge01", Assert.Single(request.Headers.GetValues("X-CodeRank-Worker")));
            Assert.True(request.Headers.GetValues(JudgePayloadContract.LeaseHeader).Single() == lease.LeaseToken.ToString("D"),
                "Wrong lease header.");
            Assert.Null(request.Content);
            return Task.FromResult(Json(expected));
        })) { MaxResponseContentBufferSize = 8 * 1024 };

        var read = await new JudgeApiClient(http, settings).GetPayloadAsync(lease, Token);
        Assert.Equal(PayloadReadState.Ready, read.State);
        Assert.NotNull(read.Payload);
        Assert.Equal(expected.SourceCode, read.Payload.SourceCode);
        Assert.Equal(expected.TestCases[0].Input, read.Payload.TestCases[0].Input);
        Assert.Equal("synthetic-output", read.Payload.TestCases[0].ExpectedOutput);
    }

    [Theory]
    [InlineData("job-id")]
    [InlineData("problem-id")]
    [InlineData("language")]
    [InlineData("missing-field")]
    [InlineData("empty-set")]
    [InlineData("too-many-tests")]
    [InlineData("unordered-tests")]
    [InlineData("duplicate-id")]
    [InlineData("oversized-field")]
    [InlineData("oversized-set")]
    [InlineData("oversized-source")]
    [InlineData("unknown-property")]
    [InlineData("duplicate-json-property")]
    [InlineData("malformed-json")]
    public async Task Client_rejects_mismatched_malformed_and_oversized_payload_fields(string scenario)
    {
        var lease = TestLease();
        var expected = Payload(lease);
        string InvalidJson() => scenario switch
        {
            "job-id" => JsonSerializer.Serialize(Payload(lease with { Id = lease.Id + 1 }), JudgePayloadContract.JsonOptions),
            "problem-id" => JsonSerializer.Serialize(Payload(lease with { ProblemId = lease.ProblemId + 1 }), JudgePayloadContract.JsonOptions),
            "language" => JsonSerializer.Serialize(Payload(lease with { Language = "python3" }), JudgePayloadContract.JsonOptions),
            "missing-field" => "{\"id\":42,\"problemId\":12,\"language\":\"cpp17\"}",
            "empty-set" => JsonSerializer.Serialize(Payload(lease, tests: []), JudgePayloadContract.JsonOptions),
            "too-many-tests" => JsonSerializer.Serialize(Payload(lease, tests:
                Enumerable.Range(1, 33).Select(i => PrivateCase(i)).ToArray()), JudgePayloadContract.JsonOptions),
            "unordered-tests" => JsonSerializer.Serialize(Payload(lease, tests: [PrivateCase(2), PrivateCase(1)]),
                JudgePayloadContract.JsonOptions),
            "duplicate-id" => JsonSerializer.Serialize(Payload(lease, tests: [PrivateCase(1), PrivateCase(2, id: 1)]),
                JudgePayloadContract.JsonOptions),
            "oversized-field" => JsonSerializer.Serialize(Payload(lease, tests: [PrivateCase(1, new string('\u20ac', 3000))]),
                JudgePayloadContract.JsonOptions),
            "oversized-set" => JsonSerializer.Serialize(Payload(lease, tests: Enumerable.Range(1, 5)
                .Select(i => PrivateCase(i, new string('i', 8192), new string('o', 8192))).ToArray()), JudgePayloadContract.JsonOptions),
            "oversized-source" => JsonSerializer.Serialize(Payload(lease, source: new string('\u20ac', 24_000)),
                JudgePayloadContract.JsonOptions),
            "unknown-property" => JsonSerializer.Serialize(expected, JudgePayloadContract.JsonOptions).Insert(1, "\"synthetic-private-property\":1,"),
            "duplicate-json-property" => JsonSerializer.Serialize(expected, JudgePayloadContract.JsonOptions).Insert(1, "\"id\":42,"),
            _ => "{ \"synthetic-private-property\":"
        };
        using var http = new HttpClient(new MockApi((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(InvalidJson(), Encoding.UTF8, "application/json")
        })));
        var error = await Assert.ThrowsAsync<JsonException>(
            () => new JudgeApiClient(http, PayloadSettings()).GetPayloadAsync(lease, Token));
        Assert.DoesNotContain("synthetic-private-property", error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Client_enforces_byte_limit_with_and_without_content_length(bool knownLength)
    {
        var bytes = new byte[JudgePayloadContract.MaxResponseBytes + 1];
        using var http = new HttpClient(new MockApi((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = knownLength ? new ByteArrayContent(bytes) : new StreamContent(new UnknownLengthStream(bytes))
        })));
        await Assert.ThrowsAsync<HttpRequestException>(
            () => new JudgeApiClient(http, PayloadSettings()).GetPayloadAsync(TestLease(), Token));
    }

    [Fact]
    public async Task Client_applies_timeout_to_payload_body_after_headers()
    {
        using var http = new HttpClient(new MockApi((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StalledPayloadBody()
        }))) { Timeout = TimeSpan.FromMilliseconds(50) };
        var client = new JudgeApiClient(http, PayloadSettings());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetPayloadAsync(TestLease(), Token).WaitAsync(TimeSpan.FromSeconds(5), Token));
    }

    [Fact]
    public async Task Restarted_worker_fetches_the_same_valid_lease_and_resumes_heartbeats()
    {
        var settings = PayloadSettings();
        var lease = TestLease();
        for (var run = 0; run < 2; run++)
        {
            var routes = new ConcurrentQueue<string>();
            var heartbeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var http = new HttpClient(new MockApi((request, _) =>
            {
                var route = request.RequestUri!.Segments[^1];
                routes.Enqueue(route);
                if (route == "claim") return Task.FromResult(Json(lease));
                if (route == "payload") return Task.FromResult(Json(Payload(lease)));
                Assert.Equal("heartbeat", route);
                heartbeat.TrySetResult();
                return Task.FromResult(Json(new LeaseHeartbeat(lease.LeaseExpiresAtUtc)));
            }));
            var logger = new MemoryLogger();
            using var worker = new JudgeWorker(new JudgeApiClient(http, settings), settings, logger);
            await RunUntilAsync(worker, heartbeat.Task);
            Assert.Equal(new[] { "claim", "payload", "heartbeat" }, routes.Take(3));
            Assert.Equal(1, routes.Count(route => route == "payload"));
            Assert.DoesNotContain("complete", routes);
            Assert.True(logger.Messages.All(message => !message.Contains("synthetic-test-output", StringComparison.Ordinal) &&
                !message.Contains(settings.Key, StringComparison.Ordinal) && !message.Contains(lease.LeaseToken.ToString(), StringComparison.Ordinal)));
        }
    }

    [Theory]
    [InlineData(409, "lease_lost", "claim")]
    [InlineData(409, "judge_tests_unavailable", "heartbeat")]
    [InlineData(422, "judge_payload_invalid", "heartbeat")]
    public async Task Worker_drops_a_lost_lease_but_keeps_heartbeats_for_an_unavailable_payload(int status, string code, string nextRoute)
    {
        var settings = PayloadSettings();
        var lease = TestLease();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var routes = new ConcurrentQueue<string>();
        var calls = 0;
        using var http = new HttpClient(new MockApi((request, _) =>
        {
            var route = request.RequestUri!.Segments[^1];
            routes.Enqueue(route);
            switch (Interlocked.Increment(ref calls))
            {
                case 1:
                    return Task.FromResult(Json(lease));
                case 2:
                    return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = JsonContent.Create(new { code }) });
                default:
                    completed.TrySetResult();
                    return Task.FromResult(route == "heartbeat"
                        ? Json(new LeaseHeartbeat(lease.LeaseExpiresAtUtc))
                        : new HttpResponseMessage(HttpStatusCode.NoContent));
            }
        }));
        using var worker = new JudgeWorker(new JudgeApiClient(http, settings), settings, new MemoryLogger());
        await RunUntilAsync(worker, completed.Task);
        Assert.Equal(new[] { "claim", "payload", nextRoute }, routes.Take(3));
    }

    private static async Task RunUntilAsync(JudgeWorker worker, Task checkpoint)
    {
        await worker.StartAsync(Token);
        try { await checkpoint.WaitAsync(TimeSpan.FromSeconds(5), Token); }
        finally { await worker.StopAsync(Token); }
    }

    private static WorkerSettings PayloadSettings() => new(new Uri("https://api.example.test/"), "judge01", RandomKey())
    {
        FetchPayload = true,
        IdleInterval = TimeSpan.FromMilliseconds(5),
        HeartbeatInterval = TimeSpan.FromMilliseconds(5),
        InitialRetryDelay = TimeSpan.FromMilliseconds(5),
        MaximumRetryDelay = TimeSpan.FromMilliseconds(20)
    };

    private static JudgePayload Payload(JobLease lease, string source = "int main() {}",
        IReadOnlyList<JudgePayloadTest>? tests = null) => new()
        {
            Id = lease.Id,
            ProblemId = lease.ProblemId,
            Language = lease.Language,
            SourceCode = source,
            TestCases = tests ?? [PrivateCase(1)]
        };

    private static JudgePayloadTest PrivateCase(int position, string input = "synthetic-test-input",
        string output = "synthetic-test-output", long? id = null) => new()
        {
            Id = id ?? position, Position = position, Input = input, ExpectedOutput = output
        };

    private sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class StalledPayloadBody : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("The payload read must carry a cancellation token.");

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) =>
            Task.Delay(Timeout.Infinite, token);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}

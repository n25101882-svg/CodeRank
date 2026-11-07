using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CodeRank.Judge.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CodeRank.Judge.Worker.Tests;

public sealed class JudgeWorkerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string RandomKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static JobLease TestLease() => new(42, 12, "cpp17", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1));

    [Theory]
    [InlineData("http://api.example.test", "judge01", true)]
    [InlineData("https://account@api.example.test", "judge01", true)]
    [InlineData("https://api.example.test/?x=1", "judge01", true)]
    [InlineData("https://api.example.test/#queue", "judge01", true)]
    [InlineData("https://api.example.test", "Judge One", true)]
    [InlineData("https://api.example.test", "judge__one", true)]
    [InlineData("https://api.example.test", "judge01", false)]
    public void Worker_requires_secure_endpoint_and_valid_machine_credentials(string url, string workerId, bool validKey)
    {
        using var config = new ConfigurationManager();
        config["JudgeWorker:ApiBaseUrl"] = url;
        config["JudgeWorker:WorkerId"] = workerId;
        config["JudgeWorker:Key"] = validKey ? RandomKey() : null;
        Assert.Throws<WorkerConfigurationException>(() => WorkerSettings.Load(config));
    }

    [Fact]
    public async Task Api_client_authenticates_claim_and_heartbeat_and_recognizes_lost_lease()
    {
        using var config = new ConfigurationManager();
        var key = RandomKey();
        config["JudgeWorker:ApiBaseUrl"] = "https://api.example.test/coderank";
        config["JudgeWorker:WorkerId"] = "judge01";
        config["JudgeWorker:Key"] = key;
        var settings = WorkerSettings.Load(config);
        var lease = TestLease();
        var calls = 0;
        using var http = new HttpClient(new MockApi(async (request, token) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("CodeRankJudge", request.Headers.Authorization?.Scheme);
            Assert.True(request.Headers.Authorization?.Parameter == key, "Wrong machine credential.");
            Assert.Equal("judge01", Assert.Single(request.Headers.GetValues("X-CodeRank-Worker")));
            if (calls <= 2)
            {
                Assert.Equal("/coderank/api/v1/judge/jobs/claim", request.RequestUri.AbsolutePath);
                Assert.Null(request.Content);
                return calls == 1 ? new(HttpStatusCode.NoContent) : Json(lease);
            }
            Assert.Equal("/coderank/api/v1/judge/jobs/42/heartbeat", request.RequestUri.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Single(body.RootElement.EnumerateObject());
            Assert.True(body.RootElement.GetProperty("leaseToken").GetGuid() == lease.LeaseToken, "Wrong lease token.");
            return calls == 3 ? Json(new LeaseHeartbeat(lease.LeaseExpiresAtUtc.AddSeconds(15))) : new(HttpStatusCode.Conflict);
        }));
        var api = new JudgeApiClient(http, settings);
        Assert.Null(await api.ClaimAsync(Token));
        var claimed = await api.ClaimAsync(Token);
        Assert.True(lease == claimed, "Unexpected job lease.");
        Assert.True(await api.HeartbeatAsync(claimed!, Token));
        Assert.False(await api.HeartbeatAsync(claimed!, Token));
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task Malformed_claim_is_rejected_before_the_worker_treats_it_as_a_lease()
    {
        var settings = new WorkerSettings(new Uri("https://api.example.test/"), "judge01", RandomKey());
        using var http = new HttpClient(new MockApi((_, _) =>
            Task.FromResult(Json(TestLease() with { LeaseToken = Guid.Empty }))));
        await Assert.ThrowsAsync<JsonException>(() => new JudgeApiClient(http, settings).ClaimAsync(Token));
    }

    [Fact]
    public async Task Worker_recovers_from_failures_polls_renews_and_stops_without_sending_a_verdict()
    {
        var settings = new WorkerSettings(new Uri("https://api.example.test/"), "judge01", RandomKey())
        {
            IdleInterval = TimeSpan.FromMilliseconds(5),
            HeartbeatInterval = TimeSpan.FromMilliseconds(5),
            InitialRetryDelay = TimeSpan.FromMilliseconds(5),
            MaximumRetryDelay = TimeSpan.FromMilliseconds(20)
        };
        var lease = TestLease();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var routes = new ConcurrentQueue<string>();
        var logger = new MemoryLogger();
        var count = 0;
        using var http = new HttpClient(new MockApi((request, _) =>
        {
            routes.Enqueue(request.RequestUri!.Segments[^1]);
            return Interlocked.Increment(ref count) switch
            {
                1 => throw new HttpRequestException("private exception content " + settings.Key),
                2 => throw new TaskCanceledException("simulated request timeout"),
                3 => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)),
                4 => Task.FromResult(Json(lease)),
                5 => throw new HttpRequestException("simulated connection loss"),
                6 => Task.FromResult(Json(new LeaseHeartbeat(lease.LeaseExpiresAtUtc.AddSeconds(15)))),
                7 => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)),
                _ => EmptyAndSignal()
            };
        }));

        Task<HttpResponseMessage> EmptyAndSignal()
        {
            finished.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }

        using var worker = new JudgeWorker(new JudgeApiClient(http, settings), settings, logger);
        await worker.StartAsync(Token);
        try { await finished.Task.WaitAsync(TimeSpan.FromSeconds(5), Token); }
        finally { await worker.StopAsync(Token); }

        Assert.Equal(new[] { "claim", "claim", "claim", "claim", "heartbeat", "heartbeat", "heartbeat", "claim" }, routes.Take(8));
        Assert.DoesNotContain("complete", routes);
        Assert.Contains(logger.Messages, message => message.Contains("worker stopped.", StringComparison.Ordinal));
        Assert.True(logger.Messages.All(message => !message.Contains(settings.Key, StringComparison.Ordinal) &&
            !message.Contains(lease.LeaseToken.ToString(), StringComparison.Ordinal) &&
            !message.Contains("private exception content", StringComparison.Ordinal)), "Worker log contains private data.");
    }

    private static HttpResponseMessage Json<T>(T body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };

    private sealed class MockApi(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class MemoryLogger : ILogger<JudgeWorker>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using CodeRank.Judge.Protocol;
using CodeRank.Judge.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodeRank.Judge.Worker.Tests;

public sealed class LeasedSubmissionJudgeTests
{
    [Fact]
    public async Task Heartbeats_continue_during_compilation_and_32_cases_that_outlast_one_lease()
    {
        var clock = new FastClock();
        var payload = SubmissionGraderTests.Payload("cpp17", Enumerable.Repeat("output", 32).ToArray());
        using var harness = new Harness(payload);
        var runner = new SubmissionGraderTests.StubRunner(async (language, _, inputs, token) =>
        {
            Assert.Equal("cpp17", language);
            Assert.Equal(32, inputs.Count);
            var started = clock.GetTimestamp();
            await Task.Delay(TimeSpan.FromSeconds(15), clock, token);
            Assert.True(harness.Heartbeats >= 2);
            for (var i = 0; i < inputs.Count; i++)
                await Task.Delay(TimeSpan.FromSeconds(2), clock, token);
            Assert.True(clock.GetElapsedTime(started) > TimeSpan.FromSeconds(JudgePayloadContract.LeaseSeconds));
            Assert.True(harness.Heartbeats >= 5);
            return inputs.Select(_ => SubmissionGraderTests.Exited("output")).ToArray();
        });

        var result = await harness.RunAsync(runner, clock, TimeSpan.FromSeconds(10)).WaitAsync(TimeSpan.FromSeconds(12), Token);
        Assert.Equal(LeasedJudgeResult.Finished, result);
        Assert.Equal("Accepted", Assert.Single(harness.SavedVerdicts));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Lost_or_unconfirmed_lease_cancels_execution_and_suppresses_completion(bool confirmedLoss)
    {
        var clock = new FastClock();
        using var harness = new Harness(SubmissionGraderTests.Payload("python3", "output"))
        {
            OnHeartbeat = (count, accepted) => count == 1 ? accepted() :
                confirmedLoss ? Lost() : throw new HttpRequestException("synthetic uncertain renewal")
        };
        var canceled = false;
        var runner = new SubmissionGraderTests.StubRunner(async (_, _, _, token) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { canceled = token.IsCancellationRequested; }
            return [SubmissionGraderTests.Exited("output")];
        });
        var result = await harness.RunAsync(runner, clock, TimeSpan.FromSeconds(1)).WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(confirmedLoss ? LeasedJudgeResult.LeaseEnded : LeasedJudgeResult.Halt, result);
        Assert.True(canceled);
        Assert.Empty(harness.SavedVerdicts);
        Assert.Equal(confirmedLoss ? 2 : 3, harness.Heartbeats);
    }

    [Fact]
    public async Task Final_heartbeat_conflict_prevents_posting_a_finished_verdict()
    {
        using var harness = new Harness(SubmissionGraderTests.Payload("python3", "output"))
        {
            OnHeartbeat = (count, accepted) => count == 1 ? accepted() : Lost()
        };

        Assert.Equal(LeasedJudgeResult.LeaseEnded, await harness.RunAsync(ImmediateOutput(), TimeProvider.System));
        Assert.Equal(2, harness.Heartbeats);
        Assert.Empty(harness.SavedVerdicts);
    }

    [Fact]
    public async Task Lost_complete_response_replays_the_identical_verdict_without_rerunning_sandbox()
    {
        using var harness = new Harness(SubmissionGraderTests.Payload("python3", "output"))
        {
            OnComplete = attempt => attempt == 1 ? throw new HttpRequestException("synthetic lost response") :
                new HttpResponseMessage(HttpStatusCode.NoContent)
        };
        var executions = 0;
        var runner = new SubmissionGraderTests.StubRunner((_, _, _, _) =>
        {
            executions++;
            return Task.FromResult<IReadOnlyList<IsolatedCaseResult>>([SubmissionGraderTests.Exited("output")]);
        });

        Assert.Equal(LeasedJudgeResult.Finished, await harness.RunAsync(runner, new FastClock()));
        Assert.Equal(1, executions);
        Assert.Equal(["Accepted", "Accepted"], harness.SavedVerdicts);
        Assert.Equal(2, harness.Heartbeats);
    }

    [Fact]
    public async Task Infrastructure_error_posts_system_error_once_and_halts_without_logging_private_data()
    {
        var payload = SubmissionGraderTests.Payload("python3", "private-expected-canary");
        using var harness = new Harness(payload);
        var logger = new RecordingLogger();
        var runner = new SubmissionGraderTests.StubRunner((_, _, _, _) => throw new IOException(
            payload.SourceCode + payload.TestCases[0].Input + payload.TestCases[0].ExpectedOutput +
            harness.Settings.Key + harness.Job.LeaseToken));

        Assert.Equal(LeasedJudgeResult.Halt, await harness.RunAsync(runner, TimeProvider.System, logger: logger));
        Assert.Equal("SystemError", Assert.Single(harness.SavedVerdicts));
        var text = string.Join("\n", logger.Messages);
        Assert.True(new[] { payload.SourceCode, payload.TestCases[0].Input, payload.TestCases[0].ExpectedOutput,
            harness.Settings.Key, harness.Job.LeaseToken.ToString() }.All(value => !text.Contains(value, StringComparison.Ordinal)),
            "The worker log contained private data.");
    }

    [Fact]
    public async Task Disabled_completion_halts_instead_of_reexecuting()
    {
        using var harness = new Harness(SubmissionGraderTests.Payload("python3", "output"))
        {
            OnComplete = _ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        };
        Assert.Equal(LeasedJudgeResult.Halt, await harness.RunAsync(ImmediateOutput(), TimeProvider.System));
        Assert.Single(harness.SavedVerdicts);
    }

    private static SubmissionGraderTests.StubRunner ImmediateOutput() =>
        new((_, _, _, _) => Task.FromResult<IReadOnlyList<IsolatedCaseResult>>([SubmissionGraderTests.Exited("output")]));

    private static HttpResponseMessage Lost() => new(HttpStatusCode.Conflict)
    {
        Content = JsonContent.Create(new { code = JudgePayloadContract.LeaseLostCode })
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Harness(JudgePayload payload) : IDisposable
    {
        private int heartbeats;
        private HttpClient? http;
        public int Heartbeats => Volatile.Read(ref heartbeats);
        public List<string> SavedVerdicts { get; } = [];
        public JobLease Job { get; } = new(payload.Id, payload.ProblemId, payload.Language, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1));
        public WorkerSettings Settings { get; private set; } = null!;
        public Func<int, Func<HttpResponseMessage>, HttpResponseMessage>? OnHeartbeat { get; init; }
        public Func<int, HttpResponseMessage>? OnComplete { get; init; }

        public Task<LeasedJudgeResult> RunAsync(IIsolatedJudgeRunner runner, TimeProvider clock,
            TimeSpan? interval = null, ILogger<LeasedSubmissionJudge>? logger = null)
        {
            Settings = new(new Uri("https://api.example.test/"), "judgeqa", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))
            {
                HeartbeatInterval = interval ?? TimeSpan.FromSeconds(15)
            };
            http = new(new TestHandler(SendAsync));
            var evaluator = new LeasedSubmissionJudge(new JudgeApiClient(http, Settings), new SubmissionGrader(runner),
                Settings, clock, logger ?? NullLogger<LeasedSubmissionJudge>.Instance);
            return evaluator.RunAsync(Job, payload, Token);
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.True(request.Headers.Authorization?.Parameter == Settings.Key, "Wrong worker credential.");
            using var body = await System.Text.Json.JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync(token),
                cancellationToken: token);
            Assert.True(body.RootElement.GetProperty("leaseToken").GetGuid() == Job.LeaseToken, "Wrong lease token.");
            if (request.RequestUri!.Segments[^1] == "heartbeat")
            {
                var count = Interlocked.Increment(ref heartbeats);
                HttpResponseMessage Accepted() => new(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new LeaseHeartbeat(Job.LeaseExpiresAtUtc))
                };
                return OnHeartbeat is null ? Accepted() : OnHeartbeat(count, Accepted);
            }

            Assert.Equal("complete", request.RequestUri.Segments[^1]);
            Assert.Equal(2, body.RootElement.EnumerateObject().Count());
            SavedVerdicts.Add(body.RootElement.GetProperty("verdict").GetString()!);
            return OnComplete?.Invoke(SavedVerdicts.Count) ?? new(HttpStatusCode.NoContent);
        }

        public void Dispose() => http?.Dispose();
    }

    private sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    private sealed class FastClock : TimeProvider
    {
        private const int Scale = 20;
        private readonly long origin = Stopwatch.GetTimestamp();
        public override long TimestampFrequency => Stopwatch.Frequency;
        public override long GetTimestamp() => (Stopwatch.GetTimestamp() - origin) * Scale;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new ScaledTimer(new Timer(callback, state, Real(dueTime), Real(period)));

        private static TimeSpan Real(TimeSpan time) => time == Timeout.InfiniteTimeSpan ? time : time / Scale;

        private sealed class ScaledTimer(Timer timer) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(Real(dueTime), Real(period));
            public void Dispose() => timer.Dispose();
            public ValueTask DisposeAsync() => timer.DisposeAsync();
        }
    }

    private sealed class RecordingLogger : ILogger<LeasedSubmissionJudge>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}

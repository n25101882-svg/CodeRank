using System.Net;
using System.Net.Http.Json;
using CodeRank.Api.Contracts.Judge;
using CodeRank.Api.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CodeRank.Api.Auth.Tests;

public sealed partial class JudgeJobsApiTests
{
    private const string TestsUnavailable = "judge_tests_unavailable";
    private const string InvalidPayload = "judge_payload_invalid";
    private const string FailureNotConfirmed = "judge_payload_failure_not_confirmed";

    [Theory]
    [InlineData(TestsUnavailable, HttpStatusCode.Conflict)]
    [InlineData(InvalidPayload, HttpStatusCode.UnprocessableEntity)]
    public async Task Confirmed_failure_finishes_the_submission_and_the_same_worker_claims_the_next_one(
        string reason, HttpStatusCode payloadStatus)
    {
        using var queue = new QueueHarness();
        var seed = await queue.SeedAsync(2);
        if (reason == InvalidPayload) await AddTestCases(queue, TestCase(seed.ProblemId, 1));
        using var worker = queue.Worker();
        var lease = await Reserve(worker);
        Assert.Equal(seed.Ids[0], lease.Id);
        if (reason == InvalidPayload) await CorruptSnapshot(queue, lease.Id);

        using var payload = await GetPayload(worker, lease.Id, lease.LeaseToken);
        await AssertProblem(payload, payloadStatus, reason);
        queue.Clock.Advance(TimeSpan.FromSeconds(3));
        using var finished = await FailPayload(worker, lease, reason);
        Assert.Equal(HttpStatusCode.NoContent, finished.StatusCode);
        Assert.Empty(await finished.Content.ReadAsStringAsync(Token));
        Assert.True(finished.Headers.CacheControl?.NoStore);
        var failed = await queue.SubmissionAsync(lease.Id);
        Assert.Equal(Submission.SystemError, failed.Status);
        Assert.Equal(queue.Clock.UtcNow.UtcDateTime, failed.JudgeFinishedAtUtc);
        Assert.Null(failed.JudgeLeaseExpiresAtUtc);
        Assert.Equal("judge01", failed.JudgeWorkerId);
        Assert.Equal(lease.LeaseToken, failed.JudgeLeaseToken);
        Assert.Equal(1, failed.JudgeAttemptCount);

        using var heartbeat = await Heartbeat(worker, lease);
        await AssertProblem(heartbeat, HttpStatusCode.Conflict, "lease_lost");
        using var oldPayload = await GetPayload(worker, lease.Id, lease.LeaseToken);
        await AssertProblem(oldPayload, HttpStatusCode.Conflict, "lease_lost");

        if (reason == TestsUnavailable) await AddTestCases(queue, TestCase(seed.ProblemId, 1));
        var next = await Reserve(worker);
        Assert.Equal(seed.Ids[1], next.Id);
        using var nextPayload = await GetPayload(worker, next.Id, next.LeaseToken);
        Assert.Equal(HttpStatusCode.OK, nextPayload.StatusCode);
        using var student = queue.Student(seed.Owner);
        await AssertStudentStatus(student, lease.Id, Submission.SystemError);
    }

    [Fact]
    public async Task Failure_receipt_survives_api_restart_and_keeps_the_original_finish_time()
    {
        using var queue = new QueueHarness();
        var seed = await queue.SeedAsync(1);
        using var worker = queue.Worker();
        var lease = await Reserve(worker);
        using var first = await FailPayload(worker, lease, TestsUnavailable);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        var finishedAt = (await queue.SubmissionAsync(lease.Id)).JudgeFinishedAtUtc;
        Assert.NotNull(finishedAt);

        await AddTestCases(queue, TestCase(seed.ProblemId, 1));
        queue.Clock.Advance(TimeSpan.FromMinutes(2));
        queue.RestartApi(acceptPrevious: false);
        using var restarted = queue.Worker();
        using var replay = await FailPayload(restarted, lease, TestsUnavailable);
        Assert.Equal(HttpStatusCode.NoContent, replay.StatusCode);
        var saved = await queue.SubmissionAsync(lease.Id);
        Assert.Equal(finishedAt, saved.JudgeFinishedAtUtc);
        Assert.Equal(1, saved.JudgeAttemptCount);
        using var mismatch = await FailPayload(restarted, lease, InvalidPayload);
        await AssertProblem(mismatch, HttpStatusCode.Conflict, FailureNotConfirmed);
        using var empty = await Claim(restarted);
        Assert.Equal(HttpStatusCode.NoContent, empty.StatusCode);
    }

    [Theory]
    [InlineData("ready", TestsUnavailable)]
    [InlineData("ready", InvalidPayload)]
    [InlineData("missing", InvalidPayload)]
    [InlineData("invalid", TestsUnavailable)]
    public async Task Unsupported_failure_claims_do_not_change_the_lease(string state, string reason)
    {
        using var queue = new QueueHarness();
        var seed = await queue.SeedAsync(1);
        if (state != "missing") await AddTestCases(queue, TestCase(seed.ProblemId, 1));
        using var worker = queue.Worker();
        var lease = await Reserve(worker);
        if (state == "invalid") await CorruptSnapshot(queue, lease.Id);

        using var denied = await FailPayload(worker, lease, reason);
        await AssertProblem(denied, HttpStatusCode.Conflict, FailureNotConfirmed);
        await AssertPrivateDataAbsent(denied);
        var saved = await queue.SubmissionAsync(lease.Id);
        Assert.Equal(Submission.Running, saved.Status);
        Assert.Null(saved.JudgeFinishedAtUtc);
        Assert.Equal(lease.LeaseExpiresAtUtc, saved.JudgeLeaseExpiresAtUtc);
        if (state == "ready")
        {
            using var payload = await GetPayload(worker, lease.Id, lease.LeaseToken);
            Assert.Equal(HttpStatusCode.OK, payload.StatusCode);
        }
    }

    [Theory]
    [InlineData("other-worker")]
    [InlineData("wrong-token")]
    [InlineData("unknown")]
    [InlineData("expired")]
    [InlineData("expires-during-check")]
    [InlineData("reassigned")]
    public async Task Stale_and_foreign_leases_cannot_fail_a_submission(string scenario)
    {
        using var queue = new QueueHarness();
        await queue.SeedAsync(1);
        using var worker = queue.Worker();
        using var other = queue.Worker("judge02");
        var lease = await Reserve(worker);
        if (scenario is "expired" or "reassigned") queue.Clock.Advance(TimeSpan.FromSeconds(60));
        if (scenario == "reassigned") Assert.Equal(lease.Id, (await Reserve(other)).Id);
        if (scenario == "expires-during-check")
        {
            queue.Clock.Advance(TimeSpan.FromSeconds(59));
            queue.Clock.AdvanceAfterNextRead = TimeSpan.FromSeconds(1);
        }

        var attempt = scenario switch
        {
            "wrong-token" => lease with { LeaseToken = Guid.NewGuid() },
            "unknown" => lease with { Id = long.MaxValue },
            _ => lease
        };
        using var denied = await FailPayload(scenario == "other-worker" ? other : worker, attempt, TestsUnavailable);
        await AssertProblem(denied, HttpStatusCode.Conflict, "lease_lost");
        await AssertPrivateDataAbsent(denied);
        var saved = await queue.SubmissionAsync(lease.Id);
        Assert.Equal(Submission.Running, saved.Status);
        Assert.Null(saved.JudgeFinishedAtUtc);
    }

    [Theory]
    [InlineData("User")]
    [InlineData("Admin")]
    [InlineData("wrong-key")]
    [InlineData("http")]
    public async Task Only_https_machine_credentials_can_report_a_failure(string scenario)
    {
        using var queue = new QueueHarness();
        var seed = await queue.SeedAsync(1, scenario == "Admin" ? "Admin" : "User");
        using var owner = queue.Worker();
        var lease = await Reserve(owner);
        using var denied = scenario is "User" or "Admin"
            ? queue.Student(seed.Owner, scenario)
            : queue.Worker(key: scenario == "wrong-key" ? RandomKey() : null, https: scenario != "http");
        if (scenario is "User" or "Admin") denied.DefaultRequestHeaders.Add("X-CodeRank-Worker", "judge01");

        using var response = await FailPayload(denied, lease, TestsUnavailable);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(Submission.Running, (await queue.SubmissionAsync(lease.Id)).Status);
    }

    [Theory]
    [InlineData("missing-reason")]
    [InlineData("unknown-reason")]
    [InlineData("empty-token")]
    [InlineData("extra-verdict")]
    [InlineData("query-string")]
    public async Task Fail_rejects_invalid_requests_before_mutating_the_submission(string scenario)
    {
        using var queue = new QueueHarness();
        await queue.SeedAsync(1);
        using var worker = queue.Worker();
        var lease = await Reserve(worker);
        object body = scenario switch
        {
            "missing-reason" => new { lease.LeaseToken },
            "unknown-reason" => new { lease.LeaseToken, reason = "SystemError" },
            "empty-token" => new { leaseToken = Guid.Empty, reason = TestsUnavailable },
            "extra-verdict" => new { lease.LeaseToken, reason = TestsUnavailable, verdict = "Accepted" },
            _ => new { lease.LeaseToken, reason = TestsUnavailable }
        };
        var query = scenario == "query-string" ? "?extra=1" : "";
        using var response = await worker.PostAsJsonAsync($"{Jobs}/{lease.Id}/fail{query}", body, Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(Submission.Running, (await queue.SubmissionAsync(lease.Id)).Status);
    }

    [Fact]
    public async Task Concurrent_failure_reports_write_one_terminal_transition()
    {
        using var queue = new QueueHarness();
        await queue.SeedAsync(1);
        using var worker = queue.Worker();
        using var retry = queue.Worker();
        var lease = await Reserve(worker);
        await using var db = queue.Context();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE TestJudgeFinishWrites (SubmissionId INTEGER NOT NULL);
            CREATE TRIGGER TestJudgeFinishWrite AFTER UPDATE OF Status ON Submissions
            WHEN NEW.Status = 'SystemError'
            BEGIN
                INSERT INTO TestJudgeFinishWrites VALUES (NEW.Id);
            END;
            """, Token);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<HttpResponseMessage> Attempt(HttpClient client) => Task.Run(async () =>
        {
            await start.Task.WaitAsync(Token);
            return await FailPayload(client, lease, TestsUnavailable);
        }, Token);
        var first = Attempt(worker);
        var second = Attempt(retry);
        start.SetResult();
        using var a = await first;
        using var b = await second;

        Assert.Equal(HttpStatusCode.NoContent, a.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, b.StatusCode);
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS [Value] FROM TestJudgeFinishWrites").SingleAsync(Token));
        var saved = await queue.SubmissionAsync(lease.Id);
        Assert.Equal(Submission.SystemError, saved.Status);
        Assert.Equal(1, saved.JudgeAttemptCount);
    }

    private static Task<HttpResponseMessage> FailPayload(HttpClient client, JudgeJobLease lease, string reason) =>
        client.PostAsJsonAsync($"{Jobs}/{lease.Id}/fail", new { lease.LeaseToken, reason }, Token);

    private static async Task CorruptSnapshot(QueueHarness queue, long id)
    {
        await using var db = queue.Context();
        var changed = await db.JudgeTestSnapshots.Where(item => item.SubmissionId == id).ExecuteUpdateAsync(
            update => update.SetProperty(item => item.TestCasesJson, "[" + HiddenOutput), Token);
        Assert.Equal(1, changed);
    }
}

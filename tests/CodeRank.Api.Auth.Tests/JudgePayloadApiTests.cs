using System.Net;
using System.Text.Json;
using CodeRank.Api.Contracts.Judge;
using CodeRank.Api.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CodeRank.Api.Auth.Tests;

public sealed partial class JudgeJobsApiTests
{
    private const string LeaseHeader = "X-CodeRank-Lease";
    private const string HiddenInput = "synthetic-hidden-input";
    private const string HiddenOutput = "synthetic-hidden-output";

    [Fact]
    public async Task Current_owner_gets_ordered_active_tests_and_can_repeat_the_read_without_another_attempt()
    {
        using var queue = new QueueHarness();
        var seed = await queue.SeedAsync(1);
        await AddTestCases(queue,
            TestCase(seed.ProblemId, 3, "", "3\n"),
            TestCase(seed.ProblemId, 2, "disabled", "disabled", active: false),
            TestCase(seed.ProblemId, 1, "1\n", ""));
        using var worker = queue.Worker();
        var lease = await Reserve(worker);
        using var first = await GetPayload(worker, lease.Id, lease.LeaseToken);
        using var again = await GetPayload(worker, lease.Id, lease.LeaseToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.True(first.Headers.CacheControl?.NoStore);
        var body = await first.Content.ReadAsStringAsync(Token);
        Assert.Equal(body, await again.Content.ReadAsStringAsync(Token));
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal(lease.Id, root.GetProperty("id").GetInt64());
        Assert.Equal(seed.ProblemId, root.GetProperty("problemId").GetInt64());
        Assert.Equal("python3", root.GetProperty("language").GetString());
        Assert.Equal("print(42)\n", root.GetProperty("sourceCode").GetString());
        Assert.False(root.TryGetProperty("leaseToken", out _));
        Assert.False(root.TryGetProperty("userId", out _));
        var tests = root.GetProperty("testCases").EnumerateArray().ToArray();
        Assert.Equal(new[] { 1, 3 }, tests.Select(test => test.GetProperty("position").GetInt32()));
        Assert.All(tests, test => Assert.True(test.GetProperty("id").GetInt64() > 0));
        Assert.Equal("1\n", tests[0].GetProperty("input").GetString());
        Assert.Equal("", tests[0].GetProperty("expectedOutput").GetString());
        Assert.Equal("", tests[1].GetProperty("input").GetString());
        Assert.Equal("3\n", tests[1].GetProperty("expectedOutput").GetString());

        var submission = await queue.SubmissionAsync(lease.Id);
        Assert.Equal(1, submission.JudgeAttemptCount);
        Assert.Equal(lease.LeaseExpiresAtUtc, submission.JudgeLeaseExpiresAtUtc);
        await using var db = queue.Context();
        Assert.Equal(1, await db.JudgeTestSnapshots.CountAsync(Token));
    }

    [Theory]
    [InlineData("other-worker")]
    [InlineData("unknown-submission")]
    [InlineData("wrong-token")]
    [InlineData("expired")]
    [InlineData("expires-during-read")]
    public async Task Foreign_unknown_and_expired_leases_are_indistinguishable(string scenario)
    {
        using var queue = new QueueHarness();
        var seed = await queue.SeedAsync(1);
        await AddTestCases(queue, TestCase(seed.ProblemId, 1));
        using var owner = queue.Worker();
        using var other = queue.Worker("judge02");
        var lease = await Reserve(owner);
        if (scenario == "expired") queue.Clock.Advance(TimeSpan.FromSeconds(60));
        if (scenario == "expires-during-read")
        {
            queue.Clock.Advance(TimeSpan.FromSeconds(59));
            queue.Clock.AdvanceAfterNextRead = TimeSpan.FromSeconds(1);
        }
        using var response = await GetPayload(scenario == "other-worker" ? other : owner,
            scenario == "unknown-submission" ? long.MaxValue : lease.Id,
            scenario == "wrong-token" ? Guid.NewGuid() : lease.LeaseToken);

        await AssertProblem(response, HttpStatusCode.Conflict, "lease_lost");
        await AssertPrivateDataAbsent(response);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("zero")]
    [InlineData("duplicate")]
    [InlineData("query-string")]
    public async Task Payload_requires_a_single_valid_lease_header_and_a_clean_url(string scenario)
    {
        using var queue = new QueueHarness();
        var seed = await queue.SeedAsync(1);
        await AddTestCases(queue, TestCase(seed.ProblemId, 1));
        using var worker = queue.Worker();
        var lease = await Reserve(worker);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{Jobs}/{lease.Id}/payload" + (scenario == "query-string" ? "?unexpected=value" : ""));
        if (scenario != "missing")
        {
            var token = scenario switch
            {
                "invalid" => "not-a-lease-token",
                "zero" => Guid.Empty.ToString("D"),
                _ => lease.LeaseToken.ToString("D")
            };
            request.Headers.Add(LeaseHeader, token);
            if (scenario == "duplicate") request.Headers.Add(LeaseHeader, token);
        }
        using var response = await worker.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertPrivateDataAbsent(response);
    }

    [Theory]
    [InlineData("User")]
    [InlineData("Admin")]
    [InlineData("wrong-key")]
    [InlineData("http")]
    public async Task Payload_rejects_personal_jwt_bad_keys_and_insecure_transport(string scenario)
    {
        using var queue = new QueueHarness();
        var seed = await queue.SeedAsync(1, scenario == "Admin" ? "Admin" : "User");
        await AddTestCases(queue, TestCase(seed.ProblemId, 1));
        using var worker = queue.Worker();
        var lease = await Reserve(worker);
        using var denied = scenario is "User" or "Admin"
            ? queue.Student(seed.Owner, scenario)
            : queue.Worker(key: scenario == "wrong-key" ? RandomKey() : null, https: scenario != "http");
        if (scenario is "User" or "Admin") denied.DefaultRequestHeaders.Add("X-CodeRank-Worker", "judge01");
        using var response = await GetPayload(denied, lease.Id, lease.LeaseToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertPrivateDataAbsent(response);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("inactive")]
    [InlineData("oversized-field")]
    [InlineData("oversized-set")]
    public async Task Empty_or_invalid_test_sets_do_not_block_claim_or_return_partial_payload(string scenario)
    {
        using var queue = new QueueHarness();
        var seed = await queue.SeedAsync(1);
        if (scenario == "inactive") await AddTestCases(queue, TestCase(seed.ProblemId, 1, active: false));
        if (scenario == "oversized-field")
            await AddTestCases(queue, TestCase(seed.ProblemId, 1, new string('\u20ac', 3000)));
        if (scenario == "oversized-set")
        {
            var field = new string('x', 8192);
            await AddTestCases(queue, Enumerable.Range(1, 5).Select(i => TestCase(seed.ProblemId, i, field, field)).ToArray());
        }
        using var worker = queue.Worker();
        var lease = await Reserve(worker);
        using var response = await GetPayload(worker, lease.Id, lease.LeaseToken);
        await AssertProblem(response, HttpStatusCode.Conflict, "judge_tests_unavailable");
        await AssertPrivateDataAbsent(response);
        using var heartbeat = await Heartbeat(worker, lease);
        Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
        Assert.Equal(1, (await queue.SubmissionAsync(lease.Id)).JudgeAttemptCount);
        await using var db = queue.Context();
        Assert.False(await db.JudgeTestSnapshots.AnyAsync(Token));
    }

    [Fact]
    public async Task New_lease_can_pin_tests_added_after_a_claim_without_tests()
    {
        using var queue = new QueueHarness();
        var seed = await queue.SeedAsync(1);
        using var worker = queue.Worker();
        var original = await Reserve(worker);
        await AddTestCases(queue, TestCase(seed.ProblemId, 1));
        Assert.Equal(original, await Reserve(worker));
        using var beforeReclaim = await GetPayload(worker, original.Id, original.LeaseToken);
        await AssertProblem(beforeReclaim, HttpStatusCode.Conflict, "judge_tests_unavailable");

        queue.Clock.Advance(TimeSpan.FromSeconds(60));
        var current = await Reserve(worker);
        Assert.Equal(original.Id, current.Id);
        Assert.NotEqual(original.LeaseToken, current.LeaseToken);
        using var payload = await GetPayload(worker, current.Id, current.LeaseToken);
        Assert.Equal(HttpStatusCode.OK, payload.StatusCode);
        Assert.Equal(2, (await queue.SubmissionAsync(current.Id)).JudgeAttemptCount);
    }

    [Fact]
    public async Task Snapshot_survives_test_edits_api_restart_and_reassignment()
    {
        using var queue = new QueueHarness();
        var seed = await queue.SeedAsync(1);
        await AddTestCases(queue, TestCase(seed.ProblemId, 1));
        using var worker = queue.Worker();
        var lease = await Reserve(worker);
        await using (var db = queue.Context())
        {
            await db.JudgeTestCases.ExecuteUpdateAsync(update => update
                .SetProperty(test => test.Input, "edited after claim")
                .SetProperty(test => test.IsActive, false), Token);
        }
        await AddTestCases(queue, TestCase(seed.ProblemId, 2, "new case"));

        using var original = await GetPayload(worker, lease.Id, lease.LeaseToken);
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        var payload = await original.Content.ReadAsStringAsync(Token);
        Assert.Contains(HiddenInput, payload);
        Assert.DoesNotContain("edited after claim", payload);
        Assert.DoesNotContain("new case", payload);

        queue.RestartApi(acceptPrevious: false);
        using var restarted = queue.Worker();
        Assert.Equal(lease, await Reserve(restarted));
        using var resumed = await GetPayload(restarted, lease.Id, lease.LeaseToken);
        Assert.Equal(payload, await resumed.Content.ReadAsStringAsync(Token));

        queue.Clock.Advance(TimeSpan.FromSeconds(60));
        using var other = queue.Worker("judge02");
        var reassigned = await Reserve(other);
        Assert.Equal(lease.Id, reassigned.Id);
        Assert.NotEqual(lease.LeaseToken, reassigned.LeaseToken);
        using var rejected = await GetPayload(restarted, lease.Id, lease.LeaseToken);
        await AssertProblem(rejected, HttpStatusCode.Conflict, "lease_lost");
        using var replayed = await GetPayload(other, reassigned.Id, reassigned.LeaseToken);
        Assert.Equal(payload, await replayed.Content.ReadAsStringAsync(Token));
        await using var snapshots = queue.Context();
        Assert.Equal(1, await snapshots.JudgeTestSnapshots.CountAsync(Token));
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("oversized-source")]
    public async Task Invalid_stored_payload_returns_safe_error(string scenario)
    {
        using var queue = new QueueHarness();
        var seed = await queue.SeedAsync(1);
        await AddTestCases(queue, TestCase(seed.ProblemId, 1));
        using var worker = queue.Worker();
        var lease = await Reserve(worker);
        await using (var db = queue.Context())
        {
            if (scenario == "invalid-json")
                await db.JudgeTestSnapshots.ExecuteUpdateAsync(update => update.SetProperty(
                    snapshot => snapshot.TestCasesJson, "[" + HiddenOutput), Token);
            else
                await db.Submissions.ExecuteUpdateAsync(update => update.SetProperty(
                    item => item.SourceCode, new string('\u20ac', 24_000)), Token);
        }

        using var response = await GetPayload(worker, lease.Id, lease.LeaseToken);
        await AssertProblem(response, HttpStatusCode.UnprocessableEntity, "judge_payload_invalid");
        await AssertPrivateDataAbsent(response);
    }

    [Fact]
    public async Task Public_problem_and_submission_history_never_contain_private_cases_or_snapshot_fields()
    {
        using var queue = new QueueHarness();
        var seed = await queue.SeedAsync(1);
        await AddTestCases(queue, TestCase(seed.ProblemId, 1));
        await using (var db = queue.Context())
        {
            db.ProblemExamples.Add(new()
            {
                ProblemId = seed.ProblemId, Position = 1, InputData = "public-sample", ExpectedOutput = "public-output"
            });
            await db.SaveChangesAsync(Token);
        }
        using var worker = queue.Worker();
        var lease = await Reserve(worker);
        using var privatePayload = await GetPayload(worker, lease.Id, lease.LeaseToken);
        Assert.Contains(HiddenOutput, await privatePayload.Content.ReadAsStringAsync(Token));

        var submission = await queue.SubmissionAsync(lease.Id);
        using var anonymous = queue.Client();
        using var problem = await anonymous.GetAsync($"/api/v1/olympiads/{submission.OlympiadId}/problems/{seed.ProblemId}", Token);
        Assert.Equal(HttpStatusCode.OK, problem.StatusCode);
        Assert.Contains("public-sample", await problem.Content.ReadAsStringAsync(Token));
        await AssertPrivateDataAbsent(problem);
        using var student = queue.Student(seed.Owner);
        using var history = await student.GetAsync("/api/v1/submissions", Token);
        using var detail = await student.GetAsync($"/api/v1/submissions/{lease.Id}", Token);
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        await AssertPrivateDataAbsent(history);
        await AssertPrivateDataAbsent(detail, allowsOwnSource: true);
    }

    private static JudgeTestCase TestCase(long problemId, int position, string input = HiddenInput,
        string output = HiddenOutput, bool active = true) => new()
        {
            ProblemId = problemId, Position = position, Input = input, ExpectedOutput = output, IsActive = active
        };

    private static async Task AddTestCases(QueueHarness queue, params JudgeTestCase[] tests)
    {
        await using var db = queue.Context();
        db.JudgeTestCases.AddRange(tests);
        await db.SaveChangesAsync(Token);
    }

    private static async Task<HttpResponseMessage> GetPayload(HttpClient client, long id, Guid leaseToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Jobs}/{id}/payload");
        request.Headers.Add(LeaseHeader, leaseToken.ToString("D"));
        return await client.SendAsync(request, Token);
    }

    private static async Task AssertPrivateDataAbsent(HttpResponseMessage response, bool allowsOwnSource = false)
    {
        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain(HiddenInput, body);
        Assert.DoesNotContain(HiddenOutput, body);
        Assert.DoesNotContain("testCases", body);
        Assert.DoesNotContain("testCasesJson", body);
        if (!allowsOwnSource) Assert.DoesNotContain("print(42)", body);
    }
}

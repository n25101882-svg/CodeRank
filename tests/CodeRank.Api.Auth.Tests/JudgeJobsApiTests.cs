using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using CodeRank.Api.Contracts.Judge;
using CodeRank.Api.Judge;
using CodeRank.Api.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace CodeRank.Api.Auth.Tests;

public sealed partial class JudgeJobsApiTests
{
    private const string Jobs = "/api/v1/judge/jobs";
    private const string ClaimPath = Jobs + "/claim";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Empty_queue_returns_204_and_no_body()
    {
        using var queue = new QueueHarness();
        using var worker = queue.Worker();
        using var response = await Claim(worker);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Claim_returns_only_job_metadata_and_retry_reuses_the_same_reservation()
    {
        using var queue = new QueueHarness();
        var seed = await queue.SeedAsync(2);
        using var worker = queue.Worker();
        using var response = await Claim(worker);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var lease = await Read<JudgeJobLease>(response);
        Assert.Equal((seed.Ids[0], seed.ProblemId, "python3"), (lease.Id, lease.ProblemId, lease.Language));
        Assert.NotEqual(Guid.Empty, lease.LeaseToken);
        Assert.Equal(queue.Clock.UtcNow.UtcDateTime.AddSeconds(60), lease.LeaseExpiresAtUtc);
        Assert.Equal(DateTimeKind.Utc, lease.LeaseExpiresAtUtc.Kind);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(new[] { "id", "language", "leaseExpiresAtUtc", "leaseToken", "problemId" },
            json.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

        queue.Clock.Advance(TimeSpan.FromSeconds(7));
        Assert.Equal(lease, await Reserve(worker));
        var running = await queue.SubmissionAsync(lease.Id);
        Assert.Equal(("Running", 1, "judge01", lease.LeaseToken),
            (running.Status, running.JudgeAttemptCount, running.JudgeWorkerId, running.JudgeLeaseToken!.Value));
        Assert.Equal("Queued", (await queue.SubmissionAsync(seed.Ids[1])).Status);
        using var student = queue.Student(seed.Owner);
        await AssertStudentStatus(student, lease.Id, "Running");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_claims_have_one_owner_and_retries_from_one_identity_share_the_lease(bool sameIdentity)
    {
        using var queue = new QueueHarness();
        var seed = await queue.SeedAsync(sameIdentity ? 2 : 1);
        using var workerA = queue.Worker();
        using var workerB = queue.Worker(sameIdentity ? "judge01" : "judge02");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<HttpResponseMessage> Attempt(HttpClient worker) => Task.Run(async () =>
        {
            await gate.Task.WaitAsync(Token);
            return await Claim(worker);
        }, Token);
        var first = Attempt(workerA);
        var second = Attempt(workerB);
        gate.SetResult();
        using var a = await first;
        using var b = await second;

        if (sameIdentity)
        {
            Assert.Equal(HttpStatusCode.OK, a.StatusCode);
            Assert.Equal(HttpStatusCode.OK, b.StatusCode);
            Assert.Equal(await Read<JudgeJobLease>(a), await Read<JudgeJobLease>(b));
            Assert.Equal("Queued", (await queue.SubmissionAsync(seed.Ids[1])).Status);
        }
        else
        {
            Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.NoContent }, new[] { a.StatusCode, b.StatusCode }.Order());
            var winner = await Read<JudgeJobLease>(a.StatusCode == HttpStatusCode.OK ? a : b);
            Assert.Equal(seed.Ids[0], winner.Id);
            Assert.Equal(a.StatusCode == HttpStatusCode.OK ? "judge01" : "judge02",
                (await queue.SubmissionAsync(winner.Id)).JudgeWorkerId);
        }
        Assert.Equal(1, (await queue.SubmissionAsync(seed.Ids[0])).JudgeAttemptCount);
    }

    [Fact]
    public async Task Heartbeat_extends_only_the_live_owned_lease_without_accumulating_duration()
    {
        using var queue = new QueueHarness();
        await queue.SeedAsync(1);
        using var worker = queue.Worker();
        using var other = queue.Worker("judge02");
        var lease = await Reserve(worker);
        queue.Clock.Advance(TimeSpan.FromSeconds(25));

        using var foreign = await Heartbeat(other, lease);
        await AssertProblem(foreign, HttpStatusCode.Conflict, "lease_lost");
        using var wrongToken = await Heartbeat(worker, lease with { LeaseToken = Guid.NewGuid() });
        await AssertProblem(wrongToken, HttpStatusCode.Conflict, "lease_lost");
        using var missing = await Heartbeat(worker, lease with { Id = long.MaxValue });
        await AssertProblem(missing, HttpStatusCode.Conflict, "lease_lost");

        using var renew = await Heartbeat(worker, lease);
        Assert.Equal(HttpStatusCode.OK, renew.StatusCode);
        var renewed = await Read<JudgeHeartbeat>(renew);
        Assert.Equal(lease.LeaseExpiresAtUtc.AddSeconds(25), renewed.LeaseExpiresAtUtc);
        using var repeat = await Heartbeat(worker, lease);
        Assert.Equal(renewed, await Read<JudgeHeartbeat>(repeat));
        var persisted = await queue.SubmissionAsync(lease.Id);
        Assert.Equal(renewed.LeaseExpiresAtUtc, persisted.JudgeLeaseExpiresAtUtc);
        Assert.Equal(1, persisted.JudgeAttemptCount);
    }

    [Fact]
    public async Task Expiry_is_exclusive_and_reassignment_fences_the_previous_worker()
    {
        using var queue = new QueueHarness(testVerdicts: true);
        await queue.SeedAsync(1);
        using var crashed = queue.Worker();
        using var replacement = queue.Worker("judge02");
        var old = await Reserve(crashed);
        queue.Clock.Advance(TimeSpan.FromSeconds(60));
        using var tooLate = await Heartbeat(crashed, old);
        await AssertProblem(tooLate, HttpStatusCode.Conflict, "lease_lost");
        using var expiredResult = await Complete(crashed, old, "Accepted");
        await AssertProblem(expiredResult, HttpStatusCode.Conflict, "lease_lost");

        var current = await Reserve(replacement);
        Assert.Equal(old.Id, current.Id);
        Assert.NotEqual(old.LeaseToken, current.LeaseToken);
        using var stale = await Complete(crashed, old, "Accepted");
        await AssertProblem(stale, HttpStatusCode.Conflict, "lease_lost");
        using var mismatched = await Complete(crashed, current, "Accepted");
        await AssertProblem(mismatched, HttpStatusCode.Conflict, "lease_lost");
        Assert.Equal(2, (await queue.SubmissionAsync(current.Id)).JudgeAttemptCount);
        using var succeeded = await Complete(replacement, current, "WrongAnswer");
        Assert.Equal(HttpStatusCode.NoContent, succeeded.StatusCode);
    }

    [Fact]
    public async Task Three_expired_attempts_end_in_system_error_and_stay_in_student_history()
    {
        using var queue = new QueueHarness(testVerdicts: true);
        var seed = await queue.SeedAsync(1);
        using var worker = queue.Worker();
        var tokens = new HashSet<Guid>();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var lease = await Reserve(worker);
            Assert.True(tokens.Add(lease.LeaseToken));
            Assert.Equal(attempt, (await queue.SubmissionAsync(lease.Id)).JudgeAttemptCount);
            queue.Clock.Advance(TimeSpan.FromSeconds(60));
        }

        using var exhausted = await Claim(worker);
        Assert.Equal(HttpStatusCode.NoContent, exhausted.StatusCode);
        var failed = await queue.SubmissionAsync(seed.Ids[0]);
        Assert.Equal("SystemError", failed.Status);
        Assert.Equal(3, failed.JudgeAttemptCount);
        Assert.Equal(queue.Clock.UtcNow.UtcDateTime, failed.JudgeFinishedAtUtc);
        Assert.Null(failed.JudgeWorkerId);
        Assert.Null(failed.JudgeLeaseToken);
        Assert.Null(failed.JudgeLeaseExpiresAtUtc);
        using var student = queue.Student(seed.Owner);
        await AssertStudentStatus(student, failed.Id, "SystemError");
    }

    [Fact]
    public async Task Completion_is_idempotent_but_an_altered_verdict_is_rejected()
    {
        using var queue = new QueueHarness(testVerdicts: true);
        await queue.SeedAsync(1);
        using var worker = queue.Worker();
        using var other = queue.Worker("judge02");
        var lease = await Reserve(worker);
        using var invalid = await Complete(worker, lease, "Running");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var saved = await Complete(worker, lease, "Accepted");
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        var finished = (await queue.SubmissionAsync(lease.Id)).JudgeFinishedAtUtc;
        Assert.Equal(queue.Clock.UtcNow.UtcDateTime, finished);

        queue.Clock.Advance(TimeSpan.FromMinutes(2));
        using var repeat = await Complete(worker, lease, "Accepted");
        Assert.Equal(HttpStatusCode.NoContent, repeat.StatusCode);
        using var changed = await Complete(worker, lease, "WrongAnswer");
        await AssertProblem(changed, HttpStatusCode.Conflict, "verdict_conflict");
        using var foreign = await Complete(other, lease, "Accepted");
        await AssertProblem(foreign, HttpStatusCode.Conflict, "lease_lost");
        var stored = await queue.SubmissionAsync(lease.Id);
        Assert.Equal(("Accepted", finished, 1), (stored.Status, stored.JudgeFinishedAtUtc, stored.JudgeAttemptCount));
        Assert.Null(stored.JudgeLeaseExpiresAtUtc);
    }

    [Theory]
    [InlineData("Accepted")]
    [InlineData("WrongAnswer")]
    public async Task Normal_stage_one_host_refuses_verdicts_even_with_a_valid_lease(string verdict)
    {
        using var queue = new QueueHarness();
        await queue.SeedAsync(1);
        using var worker = queue.Worker();
        var lease = await Reserve(worker);
        using var response = await Complete(worker, lease, verdict);
        await AssertProblem(response, HttpStatusCode.Forbidden, "judge_execution_disabled");
        Assert.Equal("Running", (await queue.SubmissionAsync(lease.Id)).Status);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-key")]
    [InlineData("unknown-worker")]
    [InlineData("plain-http")]
    [InlineData("judge-disabled")]
    public async Task Missing_or_invalid_machine_credentials_and_insecure_transport_are_rejected(string scenario)
    {
        using var queue = new QueueHarness(enabled: scenario != "judge-disabled");
        var seed = await queue.SeedAsync(1);
        using var worker = queue.Worker(
            id: scenario == "unknown-worker" ? "judge99" : "judge01",
            key: scenario == "wrong-key" ? RandomKey() : null,
            https: scenario != "plain-http");
        if (scenario == "missing") worker.DefaultRequestHeaders.Authorization = null;
        if (scenario == "plain-http") worker.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        using var response = await Claim(worker);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("Queued", (await queue.SubmissionAsync(seed.Ids[0])).Status);
    }

    [Theory]
    [InlineData("User")]
    [InlineData("Admin")]
    public async Task Valid_personal_jwt_is_not_a_judge_credential(string role)
    {
        using var queue = new QueueHarness();
        var seed = await queue.SeedAsync(1, role);
        using var account = queue.Student(seed.Owner, role);
        await AssertStudentStatus(account, seed.Ids[0], "Queued");
        account.DefaultRequestHeaders.Add("X-CodeRank-Worker", "judge01");
        var lease = new JudgeJobLease(seed.Ids[0], seed.ProblemId, "python3", Guid.NewGuid(), DateTime.UtcNow);
        using var claim = await Claim(account);
        using var heartbeat = await Heartbeat(account, lease);
        using var complete = await Complete(account, lease, "Accepted");
        Assert.All(new[] { claim, heartbeat, complete }, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
    }

    [Fact]
    public async Task Api_restart_and_key_rotation_preserve_the_lease_and_waiting_queue()
    {
        using var queue = new QueueHarness();
        var seed = await queue.SeedAsync(2);
        JudgeJobLease before;
        using (var previous = queue.Worker(key: queue.PreviousKey))
            before = await Reserve(previous);
        Assert.Equal(seed.Ids[0], before.Id);
        queue.RestartApi(acceptPrevious: false);

        using var retired = queue.Worker(key: queue.PreviousKey);
        using var refused = await Claim(retired);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        using var restarted = queue.Worker();
        Assert.Equal(before, await Reserve(restarted));
        using var other = queue.Worker("judge02");
        Assert.Equal(seed.Ids[1], (await Reserve(other)).Id);
        Assert.Equal(1, (await queue.SubmissionAsync(before.Id)).JudgeAttemptCount);
    }

    [Fact]
    public async Task Claim_requires_empty_body_and_lease_requests_reject_extra_fields()
    {
        using var queue = new QueueHarness();
        await queue.SeedAsync(1);
        using var worker = queue.Worker();
        using var invalidClaim = await worker.PostAsJsonAsync(ClaimPath, new { workerId = "judge02" }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalidClaim.StatusCode);
        var lease = await Reserve(worker);
        using var invalidHeartbeat = await worker.PostAsJsonAsync($"{Jobs}/{lease.Id}/heartbeat",
            new { leaseToken = lease.LeaseToken, workerId = "judge02" }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalidHeartbeat.StatusCode);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid-key")]
    [InlineData("invalid-id")]
    [InlineData("shared-key")]
    public void Invalid_private_worker_configuration_fails_closed(string scenario)
    {
        using var config = new ConfigurationManager();
        var key = RandomKey();
        if (scenario != "missing")
            config["Judge:Workers:" + (scenario == "invalid-id" ? "Judge.A" : "judge01") + ":CurrentKey"] =
                scenario == "invalid-key" ? "invalid" : key;
        if (scenario == "shared-key") config["Judge:Workers:judge02:CurrentKey"] = key;
        Assert.Throws<JudgeConfigurationException>(() => JudgeWorkerSettings.FromPrivateConfiguration(config));
    }

    private static string RandomKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static Task<HttpResponseMessage> Claim(HttpClient client) => client.PostAsync(ClaimPath, null, Token);
    private static Task<HttpResponseMessage> Heartbeat(HttpClient client, JudgeJobLease lease) =>
        client.PostAsJsonAsync($"{Jobs}/{lease.Id}/heartbeat", new { lease.LeaseToken }, Token);
    private static Task<HttpResponseMessage> Complete(HttpClient client, JudgeJobLease lease, string verdict) =>
        client.PostAsJsonAsync($"{Jobs}/{lease.Id}/complete", new { lease.LeaseToken, verdict }, Token);

    private static async Task<JudgeJobLease> Reserve(HttpClient client)
    {
        using var response = await Claim(client);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Read<JudgeJobLease>(response);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Token))!;

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
    }

    private static async Task AssertStudentStatus(HttpClient client, long id, string status)
    {
        using var list = await client.GetAsync("/api/v1/submissions", Token);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var history = JsonDocument.Parse(await list.Content.ReadAsStringAsync(Token));
        var item = history.RootElement.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("id").GetInt64() == id);
        Assert.Equal(status, item.GetProperty("status").GetString());
        Assert.False(item.TryGetProperty("judgeLeaseToken", out _));

        using var detail = await client.GetAsync($"/api/v1/submissions/{id}", Token);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var saved = JsonDocument.Parse(await detail.Content.ReadAsStringAsync(Token));
        Assert.Equal(status, saved.RootElement.GetProperty("status").GetString());
        Assert.Equal("print(42)\n", saved.RootElement.GetProperty("sourceCode").GetString());
        Assert.False(saved.RootElement.TryGetProperty("judgeWorkerId", out _));
    }

    private sealed class QueueHarness : IDisposable
    {
        private readonly SqliteConnection keepAlive;
        private readonly string currentKey = RandomKey();
        private readonly string secondKey = RandomKey();
        private readonly bool enabled;
        private readonly bool testVerdicts;
        private AuthApiFactory database = null!;
        private WebApplicationFactory<Program> api = null!;
        public string PreviousKey { get; } = RandomKey();
        public QueueClock Clock { get; } = new();

        public QueueHarness(bool testVerdicts = false, bool enabled = true)
        {
            this.testVerdicts = testVerdicts;
            this.enabled = enabled;
            keepAlive = new($"Data Source=judge-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False;Foreign Keys=True");
            keepAlive.Open();
            StartApi(acceptPrevious: true);
        }

        private void StartApi(bool acceptPrevious)
        {
            database = new AuthApiFactory(sharedDatabase: keepAlive.ConnectionString) { Clock = Clock };
            using var config = new ConfigurationManager();
            config["Judge:Workers:judge01:CurrentKey"] = currentKey;
            config["Judge:Workers:judge02:CurrentKey"] = secondKey;
            if (acceptPrevious) config["Judge:Workers:judge01:PreviousKey"] = PreviousKey;
            var settings = JudgeWorkerSettings.FromPrivateConfiguration(config);
            api = database.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                if (enabled) services.AddSingleton(settings);
                if (testVerdicts) services.AddSingleton(new JudgeCompletionPolicy(Enabled: true));
            }));
        }

        public void RestartApi(bool acceptPrevious)
        {
            api.Dispose();
            database.Dispose();
            StartApi(acceptPrevious);
        }

        public HttpClient Worker(string id = "judge01", string? key = null, bool https = true)
        {
            var client = Client(https);
            client.DefaultRequestHeaders.Authorization = new("CodeRankJudge", key ?? (id == "judge02" ? secondKey : currentKey));
            client.DefaultRequestHeaders.Add("X-CodeRank-Worker", id);
            return client;
        }

        public HttpClient Student(long id, string role = "User")
        {
            var now = DateTime.UtcNow;
            var token = new JwtSecurityToken(AuthApiFactory.Issuer, AuthApiFactory.Audience,
                [new Claim("sub", id.ToString(CultureInfo.InvariantCulture)), new Claim("role", role)],
                notBefore: now.AddMinutes(-1), expires: now.AddMinutes(10),
                signingCredentials: new(new SymmetricSecurityKey(database.Key.Bytes), SecurityAlgorithms.HmacSha256));
            var client = Client();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
            return client;
        }

        public async Task<Seed> SeedAsync(int count, string role = "User")
        {
            await using var db = database.Context();
            var owner = new User
            {
                Email = "judge-test@coderank.test", NormalizedEmail = "JUDGE-TEST@CODERANK.TEST",
                PasswordHash = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), IsActive = true, Role = role
            };
            db.Users.Add(owner);
            await db.SaveChangesAsync(Token);
            var now = Clock.UtcNow.UtcDateTime;
            var olympiad = new Olympiad
            {
                Title = "Judge test contest", CreatedByUserId = owner.Id, StartsAtUtc = now.AddHours(-1),
                EndsAtUtc = now.AddHours(1), IsPublished = true
            };
            var problem = new Problem { Title = "Judge test problem", Statement = "Print 42.", CreatedByUserId = owner.Id };
            db.OlympiadProblems.Add(new OlympiadProblem { Olympiad = olympiad, Problem = problem, Position = 1 });
            await db.SaveChangesAsync(Token);
            var items = Enumerable.Range(0, count).Select(_ => new Submission
            {
                UserId = owner.Id, OlympiadId = olympiad.Id, ProblemId = problem.Id,
                Language = "python3", SourceCode = "print(42)\n", SubmittedAtUtc = now
            }).ToArray();
            db.Submissions.AddRange(items);
            await db.SaveChangesAsync(Token);
            return new Seed(owner.Id, problem.Id, items.Select(item => item.Id).ToArray());
        }

        public async Task<Submission> SubmissionAsync(long id)
        {
            await using var db = database.Context();
            return await db.Submissions.AsNoTracking().SingleAsync(item => item.Id == id, Token);
        }

        public AuthTestDbContext Context() => database.Context();

        public HttpClient Client(bool https = true) => api.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(https ? "https://localhost" : "http://localhost"), AllowAutoRedirect = false
        });

        public void Dispose()
        {
            api.Dispose();
            database.Dispose();
            keepAlive.Dispose();
        }
    }

    private sealed record Seed(long Owner, long ProblemId, long[] Ids);

    private sealed class QueueClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2030, 6, 1, 10, 0, 0, TimeSpan.Zero);
        public TimeSpan AdvanceAfterNextRead { get; set; }
        public override DateTimeOffset GetUtcNow()
        {
            var value = UtcNow;
            Advance(AdvanceAfterNextRead);
            AdvanceAfterNextRead = TimeSpan.Zero;
            return value;
        }
        public void Advance(TimeSpan elapsed) => UtcNow += elapsed;
    }
}

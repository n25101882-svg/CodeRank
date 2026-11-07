using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeRank.Api.Contracts.Olympiads;
using CodeRank.Api.Contracts.Submissions;
using CodeRank.Api.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace CodeRank.Api.Auth.Tests;

public sealed class SubmissionsApiTests
{
    private const string History = "/api/v1/submissions";
    private static readonly DateTimeOffset Start = new(2030, 6, 1, 10, 0, 0, TimeSpan.Zero);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Active_user_submits_and_reads_exact_unicode_source_via_location()
    {
        using var factory = Factory();
        var contest = await SeedAsync(factory);
        using var client = SignIn(factory, contest.Participant);
        const string source = "  # Привіт 🚀\r\nprint(\"<тест>&\")\n";
        using var created = await Submit(client, contest.Path, "python3", source);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.True(created.Headers.CacheControl?.NoStore);
        var receipt = await Read<CreatedSubmission>(created);
        Assert.Equal("Queued", receipt.Status);
        Assert.Equal(Start.UtcDateTime, receipt.SubmittedAtUtc);
        Assert.Equal(DateTimeKind.Utc, receipt.SubmittedAtUtc.Kind);
        Assert.EndsWith($"{History}/{receipt.Id}", created.Headers.Location!.ToString());
        Assert.DoesNotContain("sourceCode", await created.Content.ReadAsStringAsync(Token));

        using var detail = await client.GetAsync(created.Headers.Location, Token);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.True(detail.Headers.CacheControl?.NoStore);
        var attempt = await Read<SubmissionDetail>(detail);
        Assert.Equal((receipt.Id, contest.Olympiad, contest.Problem, "python3", source, "Queued"),
            (attempt.Id, attempt.OlympiadId, attempt.ProblemId, attempt.Language, attempt.SourceCode, attempt.Status));
        using var db = factory.Context();
        Assert.Equal(contest.Participant, (await db.Submissions.SingleAsync(Token)).UserId);
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("invalid-jwt", HttpStatusCode.Unauthorized)]
    [InlineData("inactive", HttpStatusCode.Forbidden)]
    [InlineData("missing", HttpStatusCode.Forbidden)]
    public async Task Every_route_requires_a_valid_jwt_and_active_account(string actor, HttpStatusCode status)
    {
        using var factory = Factory();
        var contest = await SeedAsync(factory, active: actor != "inactive");
        using var client = actor is "anonymous" or "invalid-jwt" ? factory.Client()
            : SignIn(factory, actor == "missing" ? long.MaxValue : contest.Participant);
        if (actor == "invalid-jwt") client.DefaultRequestHeaders.Authorization = new("Bearer", "invalid.jwt.signature");

        using var post = await Submit(client, contest.Path);
        using var list = await client.GetAsync(History, Token);
        using var detail = await client.GetAsync($"{History}/1", Token);
        Assert.All(new[] { post, list, detail }, response => Assert.Equal(status, response.StatusCode));
        await AssertEmpty(factory);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("hidden")]
    [InlineData("different-olympiad")]
    [InlineData("missing-olympiad")]
    [InlineData("missing-problem")]
    public async Task Unavailable_contest_or_problem_returns_404(string scenario)
    {
        using var factory = Factory();
        var contest = await SeedAsync(factory, published: scenario != "draft", visible: scenario != "hidden");
        using var client = SignIn(factory, contest.Participant);
        var olympiad = scenario == "different-olympiad"
            ? await AddOlympiad(factory, contest.Admin) : contest.Olympiad;
        if (scenario == "missing-olympiad") olympiad = long.MaxValue;
        var problem = scenario == "missing-problem" ? long.MaxValue : contest.Problem;
        using var response = await Submit(client, Path(olympiad, problem));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertEmpty(factory);
    }

    [Theory]
    [InlineData(-1, HttpStatusCode.Conflict, "olympiad_not_started")]
    [InlineData(0, HttpStatusCode.Created, null)]
    [InlineData(1799, HttpStatusCode.Created, null)]
    [InlineData(1800, HttpStatusCode.Conflict, "olympiad_ended")]
    [InlineData(1801, HttpStatusCode.Conflict, "olympiad_ended")]
    public async Task Server_window_includes_start_and_excludes_end(int seconds, HttpStatusCode status, string? code)
    {
        using var factory = Factory(seconds);
        var contest = await SeedAsync(factory);
        using var client = SignIn(factory, contest.Participant);
        using var response = await Submit(client, contest.Path);
        Assert.Equal(status, response.StatusCode);
        if (code is null)
            Assert.Equal(Start.AddSeconds(seconds).UtcDateTime, (await Read<CreatedSubmission>(response)).SubmittedAtUtc);
        else
        {
            await AssertCode(response, code);
            await AssertEmpty(factory);
        }
    }

    [Theory]
    [InlineData("CPP17", "int main() {}")]
    [InlineData("javascript", "return 0;")]
    [InlineData("", "int main() {}")]
    [InlineData("cpp17", "")]
    [InlineData("python3", "  \r\n\t\u0085")]
    [InlineData("cpp17", null)]
    public async Task Invalid_language_or_blank_code_returns_400(string language, string? source)
    {
        using var factory = Factory();
        var contest = await SeedAsync(factory);
        using var client = SignIn(factory, contest.Participant);
        using var response = await Submit(client, contest.Path, language, source);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertEmpty(factory);
    }

    [Fact]
    public async Task Source_limit_measures_utf8_bytes()
    {
        using var factory = Factory();
        var contest = await SeedAsync(factory);
        using var client = SignIn(factory, contest.Participant);
        var limit = new string('ї', CreateSubmissionRequest.MaxSourceBytes / 2);
        using var exact = await Submit(client, contest.Path, "python3", limit);
        using var over = await Submit(client, contest.Path, "python3", limit + "a");
        Assert.Equal(HttpStatusCode.Created, exact.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
        using var db = factory.Context();
        Assert.Equal(limit, (await db.Submissions.SingleAsync(Token)).SourceCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Oversized_json_is_413_on_loopback_kestrel(bool chunked)
    {
        using var factory = Factory();
        var contest = await SeedAsync(factory);
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", IssueToken(factory, contest.Participant));
        using var request = new HttpRequestMessage(HttpMethod.Post, contest.Path)
        {
            Content = new StringContent("{\"language\":\"cpp17\",\"sourceCode\":\"int main() {}\"}"
                + new string(' ', CreateSubmissionRequest.MaxRequestBytes), Encoding.UTF8, "application/json")
        };
        if (chunked) request.Headers.TransferEncodingChunked = true;
        else request.Headers.ExpectContinue = true;
        using var response = await client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        await AssertEmpty(factory);
    }

    [Theory]
    [InlineData("userId")]
    [InlineData("id")]
    [InlineData("olympiadId")]
    [InlineData("problemId")]
    [InlineData("status")]
    [InlineData("submittedAtUtc")]
    public async Task Unknown_and_server_managed_fields_are_rejected(string field)
    {
        using var factory = Factory();
        var contest = await SeedAsync(factory);
        using var client = SignIn(factory, contest.Participant);
        using var response = await client.PostAsJsonAsync(contest.Path,
            new Dictionary<string, string> { ["language"] = "cpp17", ["sourceCode"] = "int main() {}", [field] = "value" }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertEmpty(factory);
    }

    [Fact]
    public async Task Owner_history_filters_sorts_pages_and_never_includes_foreign_attempts_or_source()
    {
        using var factory = Factory();
        var contest = await SeedAsync(factory);
        var secondOlympiad = await AddOlympiad(factory, contest.Admin);
        using var db = factory.Context();
        var secondProblem = ProblemFor(contest.Admin);
        db.Problems.Add(secondProblem);
        await db.SaveChangesAsync(Token);
        var first = Attempt(contest.Participant, contest.Olympiad, contest.Problem, Start);
        var second = Attempt(contest.Participant, contest.Olympiad, contest.Problem, Start);
        var third = Attempt(contest.Participant, secondOlympiad, contest.Problem, Start.AddMinutes(1));
        var fourth = Attempt(contest.Participant, contest.Olympiad, secondProblem.Id, Start.AddMinutes(2));
        var foreign = Attempt(contest.Other, contest.Olympiad, contest.Problem, Start.AddMinutes(3));
        db.Submissions.AddRange(first, second, third, fourth, foreign);
        await db.SaveChangesAsync(Token);
        using var client = SignIn(factory, contest.Participant);

        using var newest = await client.GetAsync(History + "?pageSize=2", Token);
        Assert.Equal(HttpStatusCode.OK, newest.StatusCode);
        var pageOne = await Read<SubmissionHistory>(newest);
        Assert.Equal(new[] { fourth.Id, third.Id }, pageOne.Items.Select(item => item.Id));
        Assert.Equal((1, 2, true), (pageOne.Page, pageOne.PageSize, pageOne.HasMore));
        var json = await newest.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("sourceCode", json);
        Assert.DoesNotContain("userId", json);
        var pageTwo = await ReadHistory(client, "?pageSize=2&page=2");
        Assert.Equal(new[] { second.Id, first.Id }, pageTwo.Items.Select(item => item.Id));
        Assert.False(pageTwo.HasMore);
        Assert.Equal(new[] { first.Id, second.Id }, (await ReadHistory(client,
            $"?olympiadId={contest.Olympiad}&problemId={contest.Problem}&sort=oldest")).Items.Select(item => item.Id));
        Assert.Equal(new[] { third.Id, second.Id, first.Id }, (await ReadHistory(client,
            $"?problemId={contest.Problem}")).Items.Select(item => item.Id));
        Assert.Empty((await ReadHistory(client, "?page=3&pageSize=2")).Items);

        using var notOwned = await client.GetAsync($"{History}/{foreign.Id}", Token);
        using var missing = await client.GetAsync($"{History}/{long.MaxValue}", Token);
        Assert.Equal(HttpStatusCode.NotFound, notOwned.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var secondClient = SignIn(factory, contest.Other);
        Assert.Equal(foreign.Id, Assert.Single((await ReadHistory(secondClient)).Items).Id);
        using var edit = await client.PutAsJsonAsync($"{History}/{first.Id}", new { status = "Accepted" }, Token);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, edit.StatusCode);
    }

    [Theory]
    [InlineData("?page=0")]
    [InlineData("?page=1001")]
    [InlineData("?pageSize=0")]
    [InlineData("?pageSize=51")]
    [InlineData("?sort=random")]
    [InlineData("?olympiadId=-1")]
    [InlineData("?problemId=0")]
    public async Task Invalid_history_bounds_return_400(string query)
    {
        using var factory = Factory();
        var contest = await SeedAsync(factory);
        using var client = SignIn(factory, contest.Participant);
        using var response = await client.GetAsync(History + query, Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Admin_detach_preserves_attempts_and_owner_crud_refuses_to_delete_the_problem()
    {
        using var factory = Factory();
        var contest = await SeedAsync(factory);
        using var participant = SignIn(factory, contest.Participant);
        using var admin = SignIn(factory, contest.Admin);
        using var created = await Submit(participant, contest.Path);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await Read<CreatedSubmission>(created)).Id;
        var adminPath = $"/api/v1/admin/olympiads/{contest.Olympiad}";
        using var details = await admin.GetAsync(adminPath, Token);
        var revision = (await Read<AdminOlympiadDetailResponse>(details)).Problems.Revision;
        using var detach = await admin.PutAsJsonAsync(adminPath + "/problems", new { revision, items = Array.Empty<object>() }, Token);
        Assert.Equal(HttpStatusCode.OK, detach.StatusCode);

        using var newAttempt = await Submit(participant, contest.Path);
        Assert.Equal(HttpStatusCode.NotFound, newAttempt.StatusCode);
        using var detail = await participant.GetAsync($"{History}/{id}", Token);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(id, Assert.Single((await ReadHistory(participant)).Items).Id);
        using var delete = await admin.DeleteAsync($"/api/v1/problems/{contest.Problem}", Token);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        await AssertCode(delete, "problem_has_submissions");

        using var db = factory.Context();
        Assert.Empty(await db.OlympiadProblems.ToListAsync(Token));
        await Assert.ThrowsAsync<SqliteException>(() => db.Problems
            .Where(problem => problem.Id == contest.Problem).ExecuteDeleteAsync(Token));
        Assert.Equal(1, await db.Submissions.CountAsync(Token));
    }

    private static AuthApiFactory Factory(int seconds = 0) => new() { Clock = new FrozenClock(Start.AddSeconds(seconds)) };
    private static string Path(long olympiad, long problem) =>
        $"/api/v1/olympiads/{olympiad}/problems/{problem}/submissions";

    private static HttpClient SignIn(AuthApiFactory factory, long id)
    {
        var client = factory.Client();
        client.DefaultRequestHeaders.Authorization = new("Bearer", IssueToken(factory, id));
        return client;
    }

    private static string IssueToken(AuthApiFactory factory, long id)
    {
        var now = DateTime.UtcNow;
        var jwt = new JwtSecurityToken(AuthApiFactory.Issuer, AuthApiFactory.Audience,
            [new Claim("sub", id.ToString(CultureInfo.InvariantCulture))], notBefore: now.AddMinutes(-1), expires: now.AddMinutes(10),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(factory.Key.Bytes), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private static User UserFor(string name, string role = "User", bool active = true) => new()
    {
        Email = name + "@coderank.test", NormalizedEmail = (name + "@coderank.test").ToUpperInvariant(),
        PasswordHash = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), IsActive = active, Role = role
    };

    private static Olympiad OlympiadFor(long admin, bool published = true) => new()
    {
        CreatedByUserId = admin, Title = "Submission contest",
        StartsAtUtc = Start.UtcDateTime, EndsAtUtc = Start.AddMinutes(30).UtcDateTime, IsPublished = published
    };

    private static Problem ProblemFor(long admin) =>
        new() { CreatedByUserId = admin, Title = "Test problem", Statement = "Print the input." };

    private static async Task<Contest> SeedAsync(AuthApiFactory factory, bool active = true, bool published = true, bool visible = true)
    {
        using var db = factory.Context();
        var admin = UserFor("admin", "Admin");
        var participant = UserFor("participant", active: active);
        var other = UserFor("other");
        db.Users.AddRange(admin, participant, other);
        await db.SaveChangesAsync(Token);
        var olympiad = OlympiadFor(admin.Id, published);
        var problem = ProblemFor(admin.Id);
        db.OlympiadProblems.Add(new OlympiadProblem { Olympiad = olympiad, Problem = problem, Position = 1, IsVisible = visible });
        await db.SaveChangesAsync(Token);
        return new Contest(olympiad.Id, problem.Id, admin.Id, participant.Id, other.Id);
    }

    private static async Task<long> AddOlympiad(AuthApiFactory factory, long admin)
    {
        using var db = factory.Context();
        var olympiad = OlympiadFor(admin);
        db.Olympiads.Add(olympiad);
        await db.SaveChangesAsync(Token);
        return olympiad.Id;
    }

    private static Submission Attempt(long user, long olympiad, long problem, DateTimeOffset time) => new()
    {
        UserId = user, OlympiadId = olympiad, ProblemId = problem, Language = "cpp17",
        SourceCode = "int main() {}", SubmittedAtUtc = time.UtcDateTime
    };

    private static Task<HttpResponseMessage> Submit(HttpClient client, string path,
        string language = "cpp17", string? sourceCode = "int main() {}") =>
        client.PostAsJsonAsync(path, new { language, sourceCode }, Token);

    private static async Task<T> Read<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Token))!;

    private static async Task<SubmissionHistory> ReadHistory(HttpClient client, string query = "")
    {
        using var response = await client.GetAsync(History + query, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Read<SubmissionHistory>(response);
    }

    private static async Task AssertCode(HttpResponseMessage response, string code)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
    }

    private static async Task AssertEmpty(AuthApiFactory factory)
    {
        using var db = factory.Context();
        Assert.False(await db.Submissions.AnyAsync(Token));
    }

    private sealed record Contest(long Olympiad, long Problem, long Admin, long Participant, long Other)
    {
        public string Path => SubmissionsApiTests.Path(Olympiad, Problem);
    }

    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

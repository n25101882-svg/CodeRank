using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using CodeRank.Api.Contracts.Olympiads;
using CodeRank.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace CodeRank.Api.Auth.Tests;

public sealed class AdminOlympiadsApiTests
{
    private const string AdminUrl = "/api/v1/admin/olympiads";
    private const string PublicUrl = "/api/v1/olympiads";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTime Start = new(2030, 7, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Admin_can_create_a_draft_list_shared_drafts_and_edit_another_admins_olympiad()
    {
        using var factory = new AuthApiFactory();
        var data = await SeedAsync(factory);
        using var client = AdminClient(factory, data.AdminId, claimRole: "User");
        using var created = await client.PostAsJsonAsync(AdminUrl, Details("  My new contest  "), Token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var olympiad = Assert.IsType<AdminOlympiadResponse>(await created.Content.ReadFromJsonAsync<AdminOlympiadResponse>(Token));
        Assert.Equal("My new contest", olympiad.Title);
        Assert.True(olympiad.IsOwn);
        Assert.False(olympiad.IsPublished);
        Assert.Equal(Start, olympiad.StartsAtUtc);
        Assert.Equal($"{AdminUrl}/{olympiad.Id}", created.Headers.Location?.AbsolutePath);

        var list = await client.GetFromJsonAsync<List<AdminOlympiadResponse>>(AdminUrl, Token);
        Assert.Contains(list!, item => item.Id == olympiad.Id && item.IsOwn && !item.IsPublished);
        Assert.Contains(list!, item => item.Id == data.OlympiadId && !item.IsOwn && !item.IsPublished);

        using var update = await client.PutAsJsonAsync($"{AdminUrl}/{data.OlympiadId}", Details("Edited"), Token);
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        await using var db = factory.Context();
        var stored = await db.Olympiads.FindAsync([olympiad.Id], Token);
        Assert.Equal(data.AdminId, stored!.CreatedByUserId);
        Assert.Equal("Edited", (await db.Olympiads.FindAsync([data.OlympiadId], Token))!.Title);
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("user", HttpStatusCode.Forbidden)]
    [InlineData("inactive", HttpStatusCode.Forbidden)]
    [InlineData("missing", HttpStatusCode.Forbidden)]
    public async Task All_admin_routes_check_the_current_database_role_and_activity(string caller, HttpStatusCode status)
    {
        using var factory = new AuthApiFactory();
        var data = await SeedAsync(factory);
        using var client = caller switch
        {
            "anonymous" => factory.Client(),
            "user" => AdminClient(factory, data.UserId),
            "inactive" => AdminClient(factory, data.InactiveAdminId),
            _ => AdminClient(factory, 99_999),
        };
        var path = $"{AdminUrl}/{data.OlympiadId}";
        using var list = await client.GetAsync(AdminUrl, Token);
        using var catalog = await client.GetAsync($"{AdminUrl}/problems", Token);
        using var detail = await client.GetAsync(path, Token);
        using var create = await client.PostAsJsonAsync(AdminUrl, Details("No access"), Token);
        using var edit = await client.PutAsJsonAsync(path, Details("No access"), Token);
        using var publication = await client.PutAsJsonAsync($"{path}/publication", new { isPublished = true }, Token);
        using var links = await client.PutAsJsonAsync($"{path}/problems", new { revision = new string('A', 64), items = Array.Empty<object>() }, Token);
        Assert.All(new[] { list, catalog, detail, create, edit, publication, links }, response => Assert.Equal(status, response.StatusCode));
        await using var db = factory.Context();
        var olympiad = Assert.Single(await db.Olympiads.ToListAsync(Token));
        Assert.False(olympiad.IsPublished);
        Assert.Equal("Shared draft", olympiad.Title);
        Assert.Empty(await db.OlympiadProblems.ToListAsync(Token));
    }

    [Theory]
    [InlineData("empty-title")]
    [InlineData("long-title")]
    [InlineData("reversed")]
    [InlineData("equal")]
    [InlineData("no-zone")]
    [InlineData("no-end")]
    [InlineData("role")]
    [InlineData("owner")]
    [InlineData("published")]
    public async Task Invalid_metadata_and_server_managed_fields_are_rejected(string invalid)
    {
        using var factory = new AuthApiFactory();
        var data = await SeedAsync(factory);
        using var client = AdminClient(factory, data.AdminId);
        var request = Details("Contest");
        switch (invalid)
        {
            case "empty-title": request["title"] = "   "; break;
            case "long-title": request["title"] = new string('x', 201); break;
            case "reversed": request["endsAtUtc"] = Start.AddMinutes(-1); break;
            case "equal": request["endsAtUtc"] = Start; break;
            case "no-zone": request["startsAtUtc"] = "2030-07-01T12:00:00"; break;
            case "no-end": request.Remove("endsAtUtc"); break;
            case "role": request["role"] = "Admin"; break;
            case "owner": request["createdByUserId"] = data.UserId; break;
            case "published": request["isPublished"] = true; break;
        }

        using var create = await client.PostAsJsonAsync(AdminUrl, request, Token);
        using var update = await client.PutAsJsonAsync($"{AdminUrl}/{data.OlympiadId}", request, Token);
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
    }

    [Fact]
    public async Task Links_allow_shared_admin_problems_and_support_atomic_swaps_scores_visibility_and_detach()
    {
        using var factory = new AuthApiFactory();
        var data = await SeedAsync(factory);
        using var client = AdminClient(factory, data.AdminId);
        var options = await client.GetFromJsonAsync<List<AdminOlympiadProblemOption>>($"{AdminUrl}/problems", Token);
        Assert.Equal(new[] { data.FirstProblemId, data.SecondProblemId }, options!.Select(item => item.Id).Order());
        var empty = await GetLinksAsync(client, data.OlympiadId);
        var linked = await SaveLinksAsync(client, data.OlympiadId, empty.Revision,
            Link(data.SecondProblemId, 2, 80, true), Link(data.FirstProblemId, 1, 30, true));
        Assert.Equal(new[] { data.FirstProblemId, data.SecondProblemId }, linked.Items.Select(item => item.ProblemId));

        var swapped = await SaveLinksAsync(client, data.OlympiadId, linked.Revision,
            Link(data.FirstProblemId, 2, 35, false), Link(data.SecondProblemId, 1, 95, true));
        Assert.Equal(new[] { data.SecondProblemId, data.FirstProblemId }, swapped.Items.Select(item => item.ProblemId));
        Assert.Equal(95, swapped.Items[0].MaxPoints);
        Assert.False(swapped.Items[1].IsVisible);
        Assert.NotEqual(linked.Revision, swapped.Revision);
        var retry = await SaveLinksAsync(client, data.OlympiadId, linked.Revision,
            Link(data.FirstProblemId, 2, 35, false), Link(data.SecondProblemId, 1, 95, true));
        Assert.Equal(swapped.Revision, retry.Revision);

        var detached = await SaveLinksAsync(client, data.OlympiadId, swapped.Revision,
            Link(data.FirstProblemId, 2, 35, false));
        Assert.Equal(data.FirstProblemId, Assert.Single(detached.Items).ProblemId);
        await using var db = factory.Context();
        Assert.NotNull(await db.Problems.FindAsync([data.SecondProblemId], Token));
    }

    [Theory]
    [InlineData("position", HttpStatusCode.BadRequest)]
    [InlineData("points", HttpStatusCode.BadRequest)]
    [InlineData("null-item", HttpStatusCode.BadRequest)]
    [InlineData("missing-visibility", HttpStatusCode.BadRequest)]
    [InlineData("duplicate-position", HttpStatusCode.Conflict)]
    [InlineData("duplicate-problem", HttpStatusCode.Conflict)]
    [InlineData("private", HttpStatusCode.NotFound)]
    [InlineData("missing", HttpStatusCode.NotFound)]
    [InlineData("stale", HttpStatusCode.Conflict)]
    public async Task Invalid_or_conflicting_link_batches_leave_the_original_links_intact(string invalid, HttpStatusCode status)
    {
        using var factory = new AuthApiFactory();
        var data = await SeedAsync(factory);
        using var client = AdminClient(factory, data.AdminId);
        var empty = await GetLinksAsync(client, data.OlympiadId);
        var original = await SaveLinksAsync(client, data.OlympiadId, empty.Revision, Link(data.FirstProblemId, 1, 30, true));
        object?[] items = invalid switch
        {
            "position" => [Link(data.FirstProblemId, 0, 50, true)],
            "points" => [Link(data.FirstProblemId, 1, -1, true)],
            "null-item" => [null],
            "missing-visibility" => [new { problemId = data.FirstProblemId, position = 1, maxPoints = 50 }],
            "duplicate-position" => [Link(data.FirstProblemId, 2, 50, true), Link(data.SecondProblemId, 2, 60, true)],
            "duplicate-problem" => [Link(data.FirstProblemId, 1, 50, true), Link(data.FirstProblemId, 2, 60, true)],
            "private" => [Link(data.PrivateProblemId, 1, 50, false)],
            "missing" => [Link(999_999, 1, 50, true)],
            _ => [Link(data.SecondProblemId, 1, 50, true)]
        };

        using var response = await client.PutAsJsonAsync($"{AdminUrl}/{data.OlympiadId}/problems",
            new { revision = invalid == "stale" ? empty.Revision : original.Revision, items }, Token);
        Assert.Equal(status, response.StatusCode);
        var after = await GetLinksAsync(client, data.OlympiadId);
        Assert.Equal(original.Revision, after.Revision);
        Assert.Equal(original.Items, after.Items);
    }

    [Fact]
    public async Task Failed_insert_rolls_back_the_prior_removal_of_links()
    {
        using var factory = new AuthApiFactory();
        var data = await SeedAsync(factory);
        using var client = AdminClient(factory, data.AdminId);
        var empty = await GetLinksAsync(client, data.OlympiadId);
        var original = await SaveLinksAsync(client, data.OlympiadId, empty.Revision, Link(data.FirstProblemId, 1, 30, true));
        factory.Interceptor = new FailLinkInsert();

        using var response = await client.PutAsJsonAsync($"{AdminUrl}/{data.OlympiadId}/problems",
            new { revision = original.Revision, items = new[] { Link(data.SecondProblemId, 1, 60, true) } }, Token);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var after = await GetLinksAsync(client, data.OlympiadId);
        Assert.Equal(original.Revision, after.Revision);
        Assert.Equal(original.Items, after.Items);
    }

    [Fact]
    public async Task Publishing_and_unpublishing_control_the_existing_public_catalog_and_problem_routes()
    {
        using var factory = new AuthApiFactory();
        var data = await SeedAsync(factory);
        using var admin = AdminClient(factory, data.AdminId);
        using var participant = factory.Client();
        var path = $"{PublicUrl}/{data.OlympiadId}";
        var empty = await GetLinksAsync(admin, data.OlympiadId);
        await SaveLinksAsync(admin, data.OlympiadId, empty.Revision,
            Link(data.FirstProblemId, 2, 50, false), Link(data.SecondProblemId, 1, 40, true));

        Assert.Empty((await participant.GetFromJsonAsync<List<OlympiadResponse>>(PublicUrl, Token))!);
        Assert.Equal(HttpStatusCode.NotFound, (await participant.GetAsync($"{path}/problems", Token)).StatusCode);
        using var publish = await admin.PutAsJsonAsync($"{AdminUrl}/{data.OlympiadId}/publication", new { isPublished = true }, Token);
        Assert.Equal(HttpStatusCode.NoContent, publish.StatusCode);
        var catalog = await participant.GetFromJsonAsync<List<OlympiadResponse>>(PublicUrl, Token);
        Assert.Equal(data.OlympiadId, Assert.Single(catalog!).Id);
        var visible = await participant.GetFromJsonAsync<List<OlympiadProblemSummary>>($"{path}/problems", Token);
        Assert.Equal(data.SecondProblemId, Assert.Single(visible!).Id);
        Assert.Equal(HttpStatusCode.NotFound, (await participant.GetAsync($"{path}/problems/{data.FirstProblemId}", Token)).StatusCode);

        using var unpublish = await admin.PutAsJsonAsync($"{AdminUrl}/{data.OlympiadId}/publication", new { isPublished = false }, Token);
        Assert.Equal(HttpStatusCode.NoContent, unpublish.StatusCode);
        Assert.Empty((await participant.GetFromJsonAsync<List<OlympiadResponse>>(PublicUrl, Token))!);
        Assert.Equal(HttpStatusCode.NotFound, (await participant.GetAsync($"{path}/problems/{data.SecondProblemId}", Token)).StatusCode);
    }

    [Fact]
    public async Task Legacy_links_to_user_problems_can_be_hidden_but_cannot_be_published()
    {
        using var factory = new AuthApiFactory();
        var data = await SeedAsync(factory);
        await using (var db = factory.Context())
        {
            db.OlympiadProblems.Add(new()
            {
                OlympiadId = data.OlympiadId, ProblemId = data.PrivateProblemId, Position = 1, MaxPoints = 50, IsVisible = true
            });
            await db.SaveChangesAsync(Token);
        }
        using var client = AdminClient(factory, data.AdminId);
        var links = await GetLinksAsync(client, data.OlympiadId);
        Assert.Null(Assert.Single(links.Items).Title);
        var publication = $"{AdminUrl}/{data.OlympiadId}/publication";
        using var rejected = await client.PutAsJsonAsync(publication, new { isPublished = true }, Token);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);

        await SaveLinksAsync(client, data.OlympiadId, links.Revision, Link(data.PrivateProblemId, 1, 50, false));
        using var accepted = await client.PutAsJsonAsync(publication, new { isPublished = true }, Token);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        using var participant = factory.Client();
        var visible = await participant.GetFromJsonAsync<List<OlympiadProblemSummary>>($"{PublicUrl}/{data.OlympiadId}/problems", Token);
        Assert.Empty(visible!);
    }

    private static Dictionary<string, object?> Details(string title) => new()
    {
        ["title"] = title, ["description"] = "A test olympiad.",
        ["startsAtUtc"] = Start, ["endsAtUtc"] = Start.AddHours(2)
    };

    private static object Link(long problemId, int position, int maxPoints, bool isVisible) =>
        new { problemId, position, maxPoints, isVisible };

    private static async Task<AdminOlympiadLinksResponse> GetLinksAsync(HttpClient client, long id) =>
        (await client.GetFromJsonAsync<AdminOlympiadDetailResponse>($"{AdminUrl}/{id}", Token))!.Problems;

    private static async Task<AdminOlympiadLinksResponse> SaveLinksAsync(
        HttpClient client, long id, string revision, params object[] items)
    {
        using var response = await client.PutAsJsonAsync($"{AdminUrl}/{id}/problems", new { revision, items }, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AdminOlympiadLinksResponse>(Token))!;
    }

    private static HttpClient AdminClient(AuthApiFactory factory, long id, string claimRole = "Admin")
    {
        var client = factory.Client();
        var now = DateTime.UtcNow;
        var jwt = new JwtSecurityToken(AuthApiFactory.Issuer, AuthApiFactory.Audience,
            [new Claim("sub", id.ToString(CultureInfo.InvariantCulture)), new Claim("role", claimRole)],
            notBefore: now.AddMinutes(-1), expires: now.AddMinutes(10),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(factory.Key.Bytes), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
        return client;
    }

    private sealed record Seed(long AdminId, long InactiveAdminId, long UserId, long OlympiadId,
        long FirstProblemId, long SecondProblemId, long PrivateProblemId);

    private static async Task<Seed> SeedAsync(AuthApiFactory factory)
    {
        await using var db = factory.Context();
        User Account(string name, string role, bool active = true) => new()
        {
            Email = $"{name}@coderank.test", NormalizedEmail = $"{name}@coderank.test".ToUpperInvariant(),
            Role = role, IsActive = active, PasswordHash = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
        };
        var admin = Account("admin", "Admin");
        var other = Account("other", "Admin", false);
        var user = Account("participant", "User");
        Problem Task(string title, User owner) => new() { Title = title, Statement = "Solve.", CreatedByUser = owner };
        var first = Task("First", admin);
        var second = Task("Second", other);
        var privateProblem = Task("Private", user);
        var olympiad = new Olympiad
        {
            Title = "Shared draft", StartsAtUtc = Start, EndsAtUtc = Start.AddHours(2), CreatedByUser = other, IsPublished = false
        };
        db.Problems.AddRange(first, second, privateProblem);
        db.Olympiads.Add(olympiad);
        await db.SaveChangesAsync(Token);
        return new(admin.Id, other.Id, user.Id, olympiad.Id, first.Id, second.Id, privateProblem.Id);
    }

    private sealed class FailLinkInsert : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<OlympiadProblem>().Any(entry => entry.State == EntityState.Added))
                throw new InvalidOperationException("Simulated insert failure");
            return ValueTask.FromResult(result);
        }
    }
}

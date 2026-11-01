using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeRank.Api.Contracts.Problems;
using CodeRank.Api.Data;
using CodeRank.Api.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace CodeRank.Api.Auth.Tests;

public sealed class ProblemsApiTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly string[] Methods = ["LIST", "GET", "POST", "PUT", "DELETE"];

    private static Dictionary<string, object?> ValidRequest() => new()
    {
        ["title"] = "  Сума двох чисел  ", ["statement"] = "  Знайдіть суму a та b.\n",
        ["inputFormat"] = "Два цілі числа.", ["outputFormat"] = "Сума чисел.", ["difficulty"] = "Easy"
    };

    private static async Task<User> AddUserAsync(AuthApiFactory factory, string role = "User")
    {
        await using var db = factory.Context();
        var email = $"{Guid.NewGuid():N}@coderank.test";
        var user = new User { Email = email, NormalizedEmail = email.ToUpperInvariant(), Role = role,
            PasswordHash = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), IsActive = true };
        db.Users.Add(user);
        await db.SaveChangesAsync(Token);
        return user;
    }

    private static async Task<Problem> AddProblemAsync(AuthApiFactory factory, long ownerId)
    {
        await using var db = factory.Context();
        var problem = new Problem { Title = "Existing problem", Statement = "Existing statement",
            CreatedByUserId = ownerId, Difficulty = "Medium" };
        db.Problems.Add(problem);
        await db.SaveChangesAsync(Token);
        return problem;
    }

    private static async Task<Olympiad> AddOlympiadAsync(AuthApiFactory factory, long ownerId, bool published = true)
    {
        await using var db = factory.Context();
        var start = DateTime.UtcNow;
        var olympiad = new Olympiad { Title = "Test olympiad", CreatedByUserId = ownerId,
            StartsAtUtc = start, EndsAtUtc = start.AddHours(2), IsPublished = published };
        db.Olympiads.Add(olympiad);
        await db.SaveChangesAsync(Token);
        return olympiad;
    }

    private static void Authorize(HttpClient client, AuthApiFactory factory, long userId, string variant = "valid")
    {
        if (variant == "missing") return;
        if (variant == "tampered")
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid.jwt.signature");
            return;
        }
        var now = DateTime.UtcNow;
        var jwt = new JwtSecurityToken(AuthApiFactory.Issuer, AuthApiFactory.Audience,
            [new Claim("sub", variant == "subject" ? "invalid-id" : userId.ToString(CultureInfo.InvariantCulture))],
            notBefore: now.AddMinutes(-10), expires: variant == "expired" ? now.AddMinutes(-5) : now.AddMinutes(10),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(factory.Key.Bytes), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, long id, object? body = null)
    {
        var path = method is "LIST" or "POST" ? "/api/v1/problems" : $"/api/v1/problems/{id}";
        var request = new HttpRequestMessage(new HttpMethod(method == "LIST" ? "GET" : method), path);
        if (method is "POST" or "PUT") request.Content = JsonContent.Create(body ?? ValidRequest());
        return SendAndDisposeAsync(client, request);
    }

    private static async Task<HttpResponseMessage> SendAndDisposeAsync(HttpClient client, HttpRequestMessage request)
    {
        using (request) return await client.SendAsync(request, Token);
    }

    [Fact]
    public async Task Active_user_can_create_read_update_and_delete_own_problem()
    {
        using var factory = new AuthApiFactory();
        var owner = await AddUserAsync(factory);
        var other = await AddUserAsync(factory);
        var foreign = await AddProblemAsync(factory, other.Id);
        using var client = factory.Client();
        Authorize(client, factory, owner.Id);
        var request = ValidRequest();

        using var created = await SendAsync(client, "POST", 0, request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = (await created.Content.ReadFromJsonAsync<ProblemResponse>(Token))!;
        Assert.Equal($"/api/v1/problems/{dto.Id}", created.Headers.Location?.AbsolutePath ?? created.Headers.Location?.ToString());
        Assert.Equal("Сума двох чисел", dto.Title);
        Assert.Equal(request["statement"], dto.Statement);
        Assert.Equal(DateTimeKind.Utc, dto.CreatedAtUtc.Kind);
        await using (var db = factory.Context())
        {
            Assert.Equal(owner.Id, (await db.Problems.SingleAsync(p => p.Id == dto.Id, Token)).CreatedByUserId);
        }
        var list = await client.GetFromJsonAsync<List<ProblemResponse>>("/api/v1/problems", Token);
        Assert.Single(list!);
        Assert.Equal(dto.Id, list![0].Id);

        request["title"] = "Оновлена задача";
        request["statement"] = "Нова умова";
        request["difficulty"] = "Hard";
        request["inputFormat"] = null;
        using var updated = await SendAsync(client, "PUT", dto.Id, request);
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        var detail = (await client.GetFromJsonAsync<ProblemResponse>($"/api/v1/problems/{dto.Id}", Token))!;
        Assert.Equal("Оновлена задача", detail.Title);
        Assert.Equal("Нова умова", detail.Statement);
        Assert.Equal("Hard", detail.Difficulty);
        Assert.Null(detail.InputFormat);
        Assert.Equal(dto.CreatedAtUtc, detail.CreatedAtUtc);

        using var deleted = await SendAsync(client, "DELETE", dto.Id);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var missing = await SendAsync(client, "GET", dto.Id);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await using var finalDb = factory.Context();
        Assert.Equal(foreign.Id, (await finalDb.Problems.SingleAsync(Token)).Id);
    }

    [Fact]
    public async Task List_with_only_foreign_problems_is_200_and_empty()
    {
        using var factory = new AuthApiFactory();
        var owner = await AddUserAsync(factory);
        var other = await AddUserAsync(factory);
        await AddProblemAsync(factory, other.Id);
        using var client = factory.Client();
        Authorize(client, factory, owner.Id);
        using var response = await SendAsync(client, "LIST", 0);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync(Token));
    }

    public static IEnumerable<object[]> UnauthorizedCases() =>
        from method in Methods from variant in new[] { "missing", "tampered", "expired", "subject" } select new object[] { method, variant };

    [Theory]
    [MemberData(nameof(UnauthorizedCases))]
    public async Task Every_endpoint_rejects_missing_or_invalid_authentication(string method, string variant)
    {
        using var factory = new AuthApiFactory();
        var owner = await AddUserAsync(factory);
        var problem = await AddProblemAsync(factory, owner.Id);
        using var client = factory.Client();
        Authorize(client, factory, owner.Id, variant);
        using var response = await SendAsync(client, method, problem.Id);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await using var db = factory.Context();
        Assert.Equal("Existing problem", (await db.Problems.SingleAsync(Token)).Title);
    }

    public static IEnumerable<object[]> InactiveCases() =>
        from method in Methods from state in new[] { "inactive", "deleted" } select new object[] { method, state };

    [Theory]
    [MemberData(nameof(InactiveCases))]
    public async Task Every_endpoint_rechecks_that_token_user_exists_and_is_active(string method, string state)
    {
        using var factory = new AuthApiFactory();
        var owner = await AddUserAsync(factory);
        using var client = factory.Client();
        Authorize(client, factory, owner.Id);
        await using (var db = factory.Context())
        {
            var current = await db.Users.SingleAsync(Token);
            if (state == "inactive") current.IsActive = false;
            else db.Users.Remove(current);
            await db.SaveChangesAsync(Token);
        }
        using var response = await SendAsync(client, method, 123);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await using var result = factory.Context();
        Assert.Equal(0, await result.Problems.CountAsync(Token));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Foreign_and_missing_ids_return_the_same_404_and_preserve_foreign_data(string method)
    {
        using var factory = new AuthApiFactory();
        var owner = await AddUserAsync(factory);
        var other = await AddUserAsync(factory);
        var foreign = await AddProblemAsync(factory, other.Id);
        using var client = factory.Client();
        Authorize(client, factory, owner.Id);
        foreach (var id in new[] { foreign.Id, long.MaxValue })
        {
            using var response = await SendAsync(client, method, id);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.DoesNotContain("Existing statement", await response.Content.ReadAsStringAsync(Token));
        }
        await using var db = factory.Context();
        var row = await db.Problems.SingleAsync(Token);
        Assert.Equal(other.Id, row.CreatedByUserId);
        Assert.Equal("Existing problem", row.Title);
    }

    public static IEnumerable<object[]> InvalidDataCases() =>
        from method in new[] { "POST", "PUT" }
        from variant in new[] { "missingTitle", "blankTitle", "longTitle", "missingStatement", "blankStatement", "wrongTitleType", "wrongInputType", "invalidDifficulty", "emptyDifficulty", "lowercaseDifficulty" }
        select new object[] { method, variant };

    [Theory]
    [MemberData(nameof(InvalidDataCases))]
    public async Task Invalid_problem_data_returns_400_without_changes(string method, string variant)
    {
        using var factory = new AuthApiFactory();
        var owner = await AddUserAsync(factory);
        var problem = await AddProblemAsync(factory, owner.Id);
        using var client = factory.Client();
        Authorize(client, factory, owner.Id);
        var request = ValidRequest();
        switch (variant)
        {
            case "missingTitle": request.Remove("title"); break;
            case "blankTitle": request["title"] = " \t "; break;
            case "longTitle": request["title"] = new string('x', 201); break;
            case "missingStatement": request.Remove("statement"); break;
            case "blankStatement": request["statement"] = "\r\n "; break;
            case "wrongTitleType": request["title"] = 123; break;
            case "wrongInputType": request["inputFormat"] = new { extra = true }; break;
            case "invalidDifficulty": request["difficulty"] = "Expert"; break;
            case "emptyDifficulty": request["difficulty"] = ""; break;
            case "lowercaseDifficulty": request["difficulty"] = "easy"; break;
        }
        using var response = await SendAsync(client, method, problem.Id, request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = factory.Context();
        Assert.Equal("Existing problem", (await db.Problems.SingleAsync(Token)).Title);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Easy")]
    [InlineData("Medium")]
    [InlineData("Hard")]
    public async Task Allowed_difficulties_and_title_boundary_can_be_created(string? difficulty)
    {
        using var factory = new AuthApiFactory();
        var owner = await AddUserAsync(factory);
        using var client = factory.Client();
        Authorize(client, factory, owner.Id);
        var request = ValidRequest();
        request["title"] = new string('x', 200);
        request["difficulty"] = difficulty;
        using var response = await SendAsync(client, "POST", 0, request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = (await response.Content.ReadFromJsonAsync<ProblemResponse>(Token))!;
        Assert.Equal(difficulty, dto.Difficulty);
        Assert.Equal(200, dto.Title.Length);
    }

    public static IEnumerable<object[]> ImmutableFields() =>
        from method in new[] { "POST", "PUT" }
        from field in new[] { "createdByUserId", "id", "createdAtUtc", "createdByUser" }
        select new object[] { method, field };

    [Theory]
    [MemberData(nameof(ImmutableFields))]
    public async Task Client_cannot_supply_owner_or_other_server_managed_fields(string method, string field)
    {
        using var factory = new AuthApiFactory();
        var owner = await AddUserAsync(factory);
        var other = await AddUserAsync(factory);
        var problem = await AddProblemAsync(factory, owner.Id);
        using var client = factory.Client();
        Authorize(client, factory, owner.Id);
        var request = ValidRequest();
        request[field] = field == "createdAtUtc" ? "2000-01-01T00:00:00Z" : other.Id;
        using var response = await SendAsync(client, method, problem.Id, request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = factory.Context();
        var row = await db.Problems.SingleAsync(Token);
        Assert.Equal(owner.Id, row.CreatedByUserId);
        Assert.Equal(problem.CreatedAtUtc, row.CreatedAtUtc);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("LIST")]
    [InlineData("GET")]
    public async Task Responses_expose_only_problem_dto_fields(string method)
    {
        using var factory = new AuthApiFactory();
        var owner = await AddUserAsync(factory);
        var problem = await AddProblemAsync(factory, owner.Id);
        using var client = factory.Client();
        Authorize(client, factory, owner.Id);
        using var response = await SendAsync(client, method, problem.Id);
        Assert.True(response.IsSuccessStatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        var record = method == "LIST" ? json.RootElement[0] : json.RootElement;
        Assert.Equal(new[] { "createdAtUtc", "difficulty", "id", "inputFormat", "outputFormat", "statement", "title" },
            record.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Linked_problem_delete_returns_safe_409_and_keeps_both_records(bool published)
    {
        using var factory = new AuthApiFactory();
        var owner = await AddUserAsync(factory);
        var problem = await AddProblemAsync(factory, owner.Id);
        var olympiad = await AddOlympiadAsync(factory, owner.Id, published);
        await using (var db = factory.Context())
        {
            db.OlympiadProblems.Add(new OlympiadProblem { OlympiadId = olympiad.Id, ProblemId = problem.Id, Position = 1 });
            await db.SaveChangesAsync(Token);
        }
        using var client = factory.Client();
        Authorize(client, factory, owner.Id);
        using var response = await SendAsync(client, "DELETE", problem.Id);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Equal("The problem is linked to an olympiad and cannot be deleted.", json.RootElement.GetProperty("title").GetString());
        await using var result = factory.Context();
        Assert.Equal(1, await result.Problems.CountAsync(Token));
        Assert.Equal(1, await result.OlympiadProblems.CountAsync(Token));
    }

    private sealed class LinkOnDelete(Func<CodeRankDbContext> context, long olympiadId) : SaveChangesInterceptor
    {
        public bool Inserted { get; private set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var problem = eventData.Context!.ChangeTracker.Entries<Problem>().SingleOrDefault(e => e.State == EntityState.Deleted);
            if (Inserted || problem is null) return result;
            await using var other = context();
            other.OlympiadProblems.Add(new OlympiadProblem { OlympiadId = olympiadId, ProblemId = problem.Entity.Id, Position = 1 });
            await other.SaveChangesAsync(cancellationToken);
            Inserted = true;
            return result;
        }
    }

    [Fact]
    public async Task Link_inserted_after_precheck_is_caught_as_409_from_real_foreign_key_failure()
    {
        using var factory = new AuthApiFactory();
        var owner = await AddUserAsync(factory);
        var problem = await AddProblemAsync(factory, owner.Id);
        var olympiad = await AddOlympiadAsync(factory, owner.Id);
        var interceptor = new LinkOnDelete(() => factory.Context(), olympiad.Id);
        factory.Interceptor = interceptor;
        using var client = factory.Client();
        Authorize(client, factory, owner.Id);
        using var response = await SendAsync(client, "DELETE", problem.Id);
        Assert.True(interceptor.Inserted);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("SQLite", body);
        Assert.DoesNotContain("FOREIGN KEY", body);
        await using var db = factory.Context();
        Assert.Equal(1, await db.Problems.CountAsync(Token));
        Assert.Equal(1, await db.OlympiadProblems.CountAsync(Token));
    }

    [Fact]
    public async Task Unlinked_problem_delete_preserves_existing_database_cascade_for_examples()
    {
        using var factory = new AuthApiFactory();
        var owner = await AddUserAsync(factory);
        var problem = await AddProblemAsync(factory, owner.Id);
        await using (var db = factory.Context())
        {
            db.ProblemExamples.Add(new ProblemExample { ProblemId = problem.Id, Position = 1, InputData = "1 2", ExpectedOutput = "3" });
            await db.SaveChangesAsync(Token);
        }
        using var client = factory.Client();
        Authorize(client, factory, owner.Id);
        using var response = await SendAsync(client, "DELETE", problem.Id);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var result = factory.Context();
        Assert.Equal(0, await result.Problems.CountAsync(Token));
        Assert.Equal(0, await result.ProblemExamples.CountAsync(Token));
    }

    private sealed class FailDelete(bool constraintError) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new DbUpdateException("Private SQL details", constraintError
                ? new SqliteException("Private constraint details", 19, 787)
                : new InvalidOperationException("Private provider details"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unrelated_database_failure_is_safe_500_and_is_not_misreported_as_link_conflict(bool constraintError)
    {
        using var factory = new AuthApiFactory();
        var owner = await AddUserAsync(factory);
        var problem = await AddProblemAsync(factory, owner.Id);
        factory.Interceptor = new FailDelete(constraintError);
        using var client = factory.Client();
        Authorize(client, factory, owner.Id);
        using var response = await SendAsync(client, "DELETE", problem.Id);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("Private", body);
        Assert.DoesNotContain("SQLite", body);
        await using var db = factory.Context();
        Assert.Equal(1, await db.Problems.CountAsync(Token));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    public async Task Malformed_json_is_400_without_writes(string method)
    {
        using var factory = new AuthApiFactory();
        var owner = await AddUserAsync(factory);
        var problem = await AddProblemAsync(factory, owner.Id);
        using var client = factory.Client();
        Authorize(client, factory, owner.Id);
        using var request = new HttpRequestMessage(new HttpMethod(method), method == "POST" ? "/api/v1/problems" : $"/api/v1/problems/{problem.Id}")
        {
            Content = new StringContent("{invalid", Encoding.UTF8, "application/json")
        };
        using var response = await client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = factory.Context();
        Assert.Equal("Existing problem", (await db.Problems.SingleAsync(Token)).Title);
    }
}

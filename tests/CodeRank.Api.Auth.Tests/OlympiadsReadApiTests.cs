using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CodeRank.Api.Contracts.Olympiads;
using CodeRank.Api.Models;
using Xunit;

namespace CodeRank.Api.Auth.Tests;

public sealed class OlympiadsReadApiTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTime Start = new(2030, 6, 1, 10, 0, 0, DateTimeKind.Utc);

    private static Olympiad Contest(long id, bool published = true, DateTime? startsAt = null) => new()
    {
        Id = id,
        Title = $"Contest {id}",
        Description = $"Description {id}",
        StartsAtUtc = startsAt ?? Start,
        EndsAtUtc = (startsAt ?? Start).AddHours(2),
        IsPublished = published,
        CreatedAtUtc = Start.AddDays(-1)
    };

    private static async Task SeedAsync(AuthApiFactory factory, params Olympiad[] olympiads)
    {
        await using var db = factory.Context();
        var creator = new User
        {
            Email = "creator@coderank.test",
            NormalizedEmail = "CREATOR@CODERANK.TEST",
            // Not a login credential; no authentication is needed by these public read tests.
            PasswordHash = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            Role = "Admin",
            IsActive = true,
            CreatedAtUtc = Start.AddDays(-2)
        };
        db.Users.Add(creator);
        foreach (var olympiad in olympiads)
        {
            olympiad.CreatedByUser = creator;
            db.Olympiads.Add(olympiad);
        }
        await db.SaveChangesAsync(Token);
    }

    [Fact]
    public async Task List_is_public_and_returns_only_published_olympiads()
    {
        using var factory = new AuthApiFactory();
        await SeedAsync(factory, Contest(1), Contest(2, published: false), Contest(3));
        using var client = factory.Client();

        using var response = await client.GetAsync("/api/v1/olympiads", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<List<OlympiadResponse>>(Token);
        Assert.NotNull(body);
        Assert.Equal(new long[] { 1, 3 }, body.Select(olympiad => olympiad.Id));
    }

    [Fact]
    public async Task List_with_only_unpublished_olympiads_returns_empty_array()
    {
        using var factory = new AuthApiFactory();
        await SeedAsync(factory, Contest(1, published: false));
        using var client = factory.Client();

        using var response = await client.GetAsync("/api/v1/olympiads", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task List_sorts_by_start_time_then_id_ascending()
    {
        using var factory = new AuthApiFactory();
        await SeedAsync(factory, Contest(10, startsAt: Start.AddDays(1)), Contest(30), Contest(20));
        using var client = factory.Client();

        var body = await client.GetFromJsonAsync<List<OlympiadResponse>>("/api/v1/olympiads", Token);

        Assert.NotNull(body);
        Assert.Equal(new long[] { 20, 30, 10 }, body.Select(olympiad => olympiad.Id));
        Assert.Equal(new[] { Start, Start, Start.AddDays(1) }, body.Select(olympiad => olympiad.StartsAtUtc));
    }

    [Fact]
    public async Task Empty_table_returns_200_and_empty_array()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.Client();

        using var response = await client.GetAsync("/api/v1/olympiads", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Get_published_olympiad_returns_200_with_public_fields_and_utc_dates(bool withoutDescription)
    {
        using var factory = new AuthApiFactory();
        var contest = Contest(101);
        if (withoutDescription) contest.Description = null;
        await SeedAsync(factory, contest);
        using var client = factory.Client();

        using var response = await client.GetAsync("/api/v1/olympiads/101", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OlympiadResponse>(Token);
        Assert.NotNull(body);
        Assert.Equal(contest.Id, body.Id);
        Assert.Equal(contest.Title, body.Title);
        Assert.Equal(contest.Description, body.Description);
        Assert.Equal(Start, body.StartsAtUtc);
        Assert.Equal(Start.AddHours(2), body.EndsAtUtc);
        Assert.Equal(DateTimeKind.Utc, body.StartsAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, body.EndsAtUtc.Kind);
    }

    [Theory]
    [InlineData(202L)]
    [InlineData(999L)]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    public async Task Missing_or_unpublished_id_returns_404_without_revealing_record(long id)
    {
        using var factory = new AuthApiFactory();
        await SeedAsync(factory, Contest(101), Contest(202, published: false));
        using var client = factory.Client();

        using var response = await client.GetAsync($"/api/v1/olympiads/{id}", Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("Contest 202", body);
        Assert.DoesNotContain("Description 202", body);
        Assert.DoesNotContain("creator@coderank.test", body);
    }

    [Theory]
    [InlineData("/api/v1/olympiads")]
    [InlineData("/api/v1/olympiads/101")]
    public async Task Response_contract_contains_only_public_dto_fields(string path)
    {
        using var factory = new AuthApiFactory();
        await SeedAsync(factory, Contest(101));
        using var client = factory.Client();

        using var response = await client.GetAsync(path, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        var record = json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement[0] : json.RootElement;
        var fields = record.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal);
        Assert.Equal(new[] { "description", "endsAtUtc", "id", "startsAtUtc", "title" }, fields);
    }
}

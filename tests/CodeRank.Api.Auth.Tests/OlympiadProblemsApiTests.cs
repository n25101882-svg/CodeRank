using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CodeRank.Api.Contracts.Olympiads;
using CodeRank.Api.Models;
using Xunit;

namespace CodeRank.Api.Auth.Tests;

public sealed class OlympiadProblemsApiTests
{
    private const string ProblemsUrl = "/api/v1/olympiads/101/problems";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTime Start = new(2030, 6, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Public_list_returns_only_visible_problems_ordered_by_link_position()
    {
        using var factory = new AuthApiFactory();
        await SeedAsync(factory);
        using var client = factory.Client();

        using var response = await client.GetAsync(ProblemsUrl, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            new[] { new OlympiadProblemSummary(200, "Sum", "Medium", 125), new(100, "Arrays", null, 50) },
            await response.Content.ReadFromJsonAsync<List<OlympiadProblemSummary>>(Token));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.All(json.RootElement.EnumerateArray(), item =>
            AssertFields(item, "difficulty", "id", "maxPoints", "title"));
    }

    [Theory]
    [InlineData(303)]
    [InlineData(404)]
    public async Task Published_olympiad_without_visible_problems_returns_empty_array(int id)
    {
        using var factory = new AuthApiFactory();
        await SeedAsync(factory);
        using var client = factory.Client();

        using var response = await client.GetAsync($"/api/v1/olympiads/{id}/problems", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Public_detail_returns_statement_formats_and_examples_ordered_by_position()
    {
        using var factory = new AuthApiFactory();
        await SeedAsync(factory);
        using var client = factory.Client();

        using var response = await client.GetAsync($"{ProblemsUrl}/200", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<OlympiadProblemDetail>(Token);
        Assert.NotNull(detail);
        Assert.Equal(200L, detail.Id);
        Assert.Equal("Sum", detail.Title);
        Assert.Equal("Medium", detail.Difficulty);
        Assert.Equal(125, detail.MaxPoints);
        Assert.Equal("Add the two integers.", detail.Statement);
        Assert.Equal("Two integers.", detail.InputFormat);
        Assert.Equal("Their sum.", detail.OutputFormat);
        Assert.Equal(
            new[] { new OlympiadProblemExample("1 2", "3", "One plus two."), new("-4 1", "-3", null) },
            detail.Examples);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        AssertFields(json.RootElement, "difficulty", "examples", "id", "inputFormat", "maxPoints",
            "outputFormat", "statement", "title");
        Assert.All(json.RootElement.GetProperty("examples").EnumerateArray(), example =>
            AssertFields(example, "expectedOutput", "explanation", "inputData"));
    }

    [Fact]
    public async Task Detail_allows_null_formats_and_no_examples()
    {
        using var factory = new AuthApiFactory();
        await SeedAsync(factory);
        using var client = factory.Client();

        var detail = await client.GetFromJsonAsync<OlympiadProblemDetail>($"{ProblemsUrl}/100", Token);

        Assert.NotNull(detail);
        Assert.Null(detail.Difficulty);
        Assert.Null(detail.InputFormat);
        Assert.Null(detail.OutputFormat);
        Assert.Empty(detail.Examples);
    }

    [Theory]
    [InlineData("/api/v1/olympiads/202/problems")]
    [InlineData("/api/v1/olympiads/202/problems/200")]
    [InlineData("/api/v1/olympiads/999/problems")]
    [InlineData("/api/v1/olympiads/999/problems/200")]
    [InlineData("/api/v1/olympiads/303/problems/200")]
    [InlineData("/api/v1/olympiads/404/problems/200")]
    [InlineData(ProblemsUrl + "/300")]
    [InlineData(ProblemsUrl + "/999")]
    [InlineData(ProblemsUrl + "/0")]
    [InlineData(ProblemsUrl + "/invalid")]
    [InlineData(ProblemsUrl + "/9223372036854775808")]
    [InlineData("/api/v1/olympiads/-1/problems")]
    [InlineData("/api/v1/olympiads/invalid/problems")]
    [InlineData("/api/v1/olympiads/9223372036854775808/problems")]
    public async Task Unavailable_olympiad_or_problem_returns_404_without_disclosing_content(string path)
    {
        using var factory = new AuthApiFactory();
        await SeedAsync(factory);
        using var client = factory.Client();

        using var response = await client.GetAsync(path, Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("Add the two integers.", body);
        Assert.DoesNotContain("Private statement.", body);
    }

    [Fact]
    public async Task Public_detail_does_not_open_the_owner_only_problem_endpoint()
    {
        using var factory = new AuthApiFactory();
        await SeedAsync(factory);
        using var client = factory.Client();

        using var publicResponse = await client.GetAsync($"{ProblemsUrl}/200", Token);
        using var ownerResponse = await client.GetAsync("/api/v1/problems/200", Token);

        Assert.Equal(HttpStatusCode.OK, publicResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, ownerResponse.StatusCode);
    }

    private static void AssertFields(JsonElement value, params string[] fields) =>
        Assert.Equal(fields, value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

    private static async Task SeedAsync(AuthApiFactory factory)
    {
        await using var db = factory.Context();
        var creator = new User
        {
            Email = "creator@coderank.test",
            NormalizedEmail = "CREATOR@CODERANK.TEST",
            PasswordHash = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            Role = "Admin",
            IsActive = true,
            CreatedAtUtc = Start.AddDays(-2)
        };
        Olympiad Contest(long id, bool published = true) => new()
        {
            Id = id, Title = $"Contest {id}", IsPublished = published, CreatedByUser = creator,
            StartsAtUtc = Start, EndsAtUtc = Start.AddHours(2), CreatedAtUtc = Start.AddDays(-1)
        };
        var published = Contest(101);
        var unpublished = Contest(202, published: false);
        var empty = Contest(303);
        var hiddenOnly = Contest(404);
        var sum = new Problem
        {
            Id = 200, Title = "Sum", Statement = "Add the two integers.", Difficulty = "Medium",
            InputFormat = "Two integers.", OutputFormat = "Their sum.",
            CreatedAtUtc = Start.AddDays(-1), CreatedByUser = creator,
            Examples = new List<ProblemExample>
            {
                new() { Id = 11, Position = 2, InputData = "-4 1", ExpectedOutput = "-3" },
                new() { Id = 12, Position = 1, InputData = "1 2", ExpectedOutput = "3", Explanation = "One plus two." }
            }
        };
        var arrays = new Problem
        {
            Id = 100, Title = "Arrays", Statement = "Read an array.",
            CreatedAtUtc = Start.AddDays(-1), CreatedByUser = creator
        };
        var hidden = new Problem
        {
            Id = 300, Title = "Hidden", Statement = "Private statement.",
            CreatedAtUtc = Start.AddDays(-1), CreatedByUser = creator
        };
        db.Olympiads.AddRange(published, unpublished, empty, hiddenOnly);
        db.OlympiadProblems.AddRange(
            new OlympiadProblem { Olympiad = published, Problem = arrays, Position = 3, MaxPoints = 50 },
            new OlympiadProblem { Olympiad = published, Problem = hidden, Position = 2, IsVisible = false },
            new OlympiadProblem { Olympiad = published, Problem = sum, Position = 1, MaxPoints = 125 },
            new OlympiadProblem { Olympiad = unpublished, Problem = sum, Position = 1 },
            new OlympiadProblem { Olympiad = hiddenOnly, Problem = sum, Position = 1, IsVisible = false });
        await db.SaveChangesAsync(Token);
    }
}

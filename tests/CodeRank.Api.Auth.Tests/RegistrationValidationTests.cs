using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using CodeRank.Api.Contracts.Auth;
using CodeRank.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CodeRank.Api.Auth.Tests;

public sealed class RegistrationValidationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string StrongPassword(int length = 40) => RandomNumberGenerator.GetHexString(length).ToLowerInvariant();
    private static string Sequence(int zero = '0', int step = 1) => string.Concat(Enumerable.Range(0, 16)
        .Select(i => char.ConvertFromUtf32(zero + (i * step % 10))));
    private static object Request(string password, string firstName = "Тест", string lastName = "Учасник") =>
        new { email = "validation@coderank.test", password, firstName, lastName };

    [Theory]
    [InlineData("firstName", "22")]
    [InlineData("lastName", "22")]
    [InlineData("firstName", "")]
    [InlineData("lastName", "   ")]
    [InlineData("firstName", "— ’ ʼ -")]
    [InlineData("lastName", "٢٢")]
    public async Task Direct_http_invalid_name_returns_400_without_either_row(string field, string value)
    {
        using var factory = new AuthApiFactory();
        using var client = factory.Client();
        var password = StrongPassword();
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", Request(password,
            field == "firstName" ? value : "Тест", field == "lastName" ? value : "Учасник"), Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertEmptyAsync(factory);
        Assert.True(!(await response.Content.ReadAsStringAsync(Token)).Contains(password, StringComparison.Ordinal),
            "Validation response must not expose a submitted password.");
    }

    [Theory]
    [InlineData("Мар’яна", "Анна-Марія")]
    [InlineData("Лук'ян", "Де Ла Крус")]
    [InlineData("Лукʼян", "李")]
    [InlineData("أحمد", "عبد الرحمن")]
    [InlineData("A\u0301на", "नमस्ते")]
    [InlineData("\U00010400", "Unicode")]
    public async Task Unicode_names_with_apostrophes_hyphens_and_marks_register(string firstName, string lastName)
    {
        using var factory = new AuthApiFactory();
        using var client = factory.Client();
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", Request(StrongPassword(), firstName, lastName), Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var db = factory.Context();
        var profile = await db.UserProfiles.SingleAsync(Token);
        Assert.Equal(firstName, profile.FirstName);
        Assert.Equal(lastName, profile.LastName);
        Assert.Equal(1, await db.Users.CountAsync(Token));
    }

    [Theory]
    [InlineData("sequence")]
    [InlineData("reverseSequence")]
    [InlineData("stepSequence")]
    [InlineData("countThroughTen")]
    [InlineData("countThroughHundred")]
    [InlineData("countDownThroughTen")]
    [InlineData("paddedCount")]
    [InlineData("arabicIndicCount")]
    [InlineData("repeatCharacter")]
    [InlineData("repeatMotif")]
    [InlineData("repeatLongMotif")]
    [InlineData("common")]
    [InlineData("commonCaseAndPadding")]
    [InlineData("fullWidthSequence")]
    [InlineData("arabicIndicSequence")]
    [InlineData("supplementarySequence")]
    [InlineData("short")]
    [InlineData("tooLong")]
    public async Task Direct_http_weak_password_returns_400_without_either_row(string variant)
    {
        var password = variant switch {
            "sequence" => Sequence(),
            "reverseSequence" => string.Concat(Sequence().Reverse()),
            "stepSequence" => Sequence(step: 2),
            "countThroughTen" => string.Concat(Enumerable.Range(1, 12)),
            "countThroughHundred" => string.Concat(Enumerable.Range(97, 7)),
            "countDownThroughTen" => string.Concat(Enumerable.Range(1, 12).Reverse()),
            "paddedCount" => string.Concat(Enumerable.Range(1, 6).Select(n => n.ToString("D2", CultureInfo.InvariantCulture))),
            "arabicIndicCount" => string.Concat(string.Concat(Enumerable.Range(1, 12))
                .Select(digit => (char)('\u0660' + digit - '0'))),
            "repeatCharacter" => new string('x', 16),
            "repeatMotif" => string.Concat(Enumerable.Repeat("ab", 8)),
            "repeatLongMotif" => string.Concat(Enumerable.Repeat(nameof(RegisterRequest.Password), 2)),
            "common" => nameof(RegisterRequest.Password).ToLowerInvariant() + string.Concat(Enumerable.Range(1, 5)),
            "commonCaseAndPadding" => " " + nameof(RegisterRequest.Password).ToUpperInvariant() + string.Concat(Enumerable.Range(1, 5)) + " ",
            "fullWidthSequence" => Sequence(0xFF10),
            "arabicIndicSequence" => Sequence(0x0660),
            "supplementarySequence" => Sequence(0x1D7CE),
            "tooLong" => StrongPassword(129),
            _ => StrongPassword(11)
        };
        using var factory = new AuthApiFactory();
        using var client = factory.Client();
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", Request(password), Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertEmptyAsync(factory);
        Assert.True(!(await response.Content.ReadAsStringAsync(Token)).Contains(password, StringComparison.Ordinal),
            "Validation response must not expose a submitted password.");
    }

    [Theory]
    [InlineData(12)]
    [InlineData(128)]
    public async Task Strong_password_boundaries_register_without_composition_requirements(int length)
    {
        using var factory = new AuthApiFactory();
        using var client = factory.Client();
        var password = StrongPassword(length);
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", Request(password), Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var db = factory.Context();
        var user = await db.Users.SingleAsync(Token);
        Assert.True(new PasswordHasher<User>().VerifyHashedPassword(user, user.PasswordHash, password)
            == PasswordVerificationResult.Success, "Original password must verify against PasswordHasher output.");
        Assert.Equal(1, await db.UserProfiles.CountAsync(Token));
    }

    [Fact]
    public async Task Nonsequential_random_numeric_password_is_not_banned()
    {
        var password = string.Concat(Enumerable.Range(0, 24).Select(_ => RandomNumberGenerator.GetInt32(10).ToString(CultureInfo.InvariantCulture)));
        using var factory = new AuthApiFactory();
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/auth/register", Request(password), Token)).StatusCode);
    }

    [Fact]
    public async Task Numeric_password_with_a_nonconsecutive_tail_is_accepted()
    {
        var password = string.Concat(Enumerable.Range(1, 11)) + "42";
        using var factory = new AuthApiFactory();
        using var client = factory.Client();
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register", Request(password), Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Password_is_not_trimmed_or_normalized_before_hashing_or_login()
    {
        var password = "  " + StrongPassword() + "Ａ\u0301  ";
        using var factory = new AuthApiFactory();
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/auth/register", Request(password), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = "validation@coderank.test", password }, Token)).StatusCode);
        foreach (var changed in new[] { password.Trim(), password.Normalize(NormalizationForm.FormKC) })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/login",
                new { email = "validation@coderank.test", password = changed }, Token)).StatusCode);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Existing_accounts_can_still_login_with_old_short_or_weak_password(bool shortPassword)
    {
        var password = shortPassword ? StrongPassword(8) : Sequence();
        using var factory = new AuthApiFactory();
        await using (var db = factory.Context())
        {
            var user = new User { Email = "legacy@coderank.test", NormalizedEmail = "LEGACY@CODERANK.TEST",
                UserProfile = new() { FirstName = "22", LastName = "22" } };
            user.PasswordHash = new PasswordHasher<User>().HashPassword(user, password);
            db.Users.Add(user);
            await db.SaveChangesAsync(Token);
        }
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = "legacy@coderank.test", password }, Token)).StatusCode);
    }

    [Theory]
    [InlineData(100, HttpStatusCode.Created)]
    [InlineData(101, HttpStatusCode.BadRequest)]
    public async Task Name_length_limit_remains_100(int length, HttpStatusCode expected)
    {
        using var factory = new AuthApiFactory();
        using var client = factory.Client();
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", Request(StrongPassword(), new string('А', length)), Token);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.BadRequest) await AssertEmptyAsync(factory);
    }

    private static async Task AssertEmptyAsync(AuthApiFactory factory)
    {
        await using var db = factory.Context();
        Assert.Equal(0, await db.Users.CountAsync(Token));
        Assert.Equal(0, await db.UserProfiles.CountAsync(Token));
    }
}

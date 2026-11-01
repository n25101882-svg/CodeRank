using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using CodeRank.Api.Contracts.Auth;
using CodeRank.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace CodeRank.Api.Auth.Tests;

public sealed class AuthenticationApiTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string Password() => Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
    private static object Registration(string email, string password) => new {
        email, password, firstName = "  Test  ", lastName = "  Student  "
    };
    private static async Task<User> ExistingAsync(AuthApiFactory factory, string password, bool active = true)
    {
        await using var db = factory.Context();
        var user = new User { Email = "existing@coderank.test", NormalizedEmail = "EXISTING@CODERANK.TEST",
            IsActive = active, UserProfile = new() { FirstName = "Existing", LastName = "Student" } };
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user,password);
        db.Users.Add(user);await db.SaveChangesAsync(Token);return user;
    }
    private static string Signed(AuthApiFactory factory, long id, string variant = "valid")
    {
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: variant == "issuer" ? "WrongIssuer" : AuthApiFactory.Issuer,
            audience: variant == "audience" ? "WrongAudience" : AuthApiFactory.Audience,
            claims: [new Claim("sub", variant == "subject" ? "not-a-user-id" : id.ToString(CultureInfo.InvariantCulture)), new Claim("role","User")],
            notBefore: now.AddHours(-1),
            expires: variant == "expired" ? now.AddMinutes(-1) : now.AddMinutes(10),
            signingCredentials: variant == "unsigned" ? null : new SigningCredentials(
                new SymmetricSecurityKey(variant == "key" ? RandomNumberGenerator.GetBytes(32) : factory.Key.Bytes),
                variant == "algorithm" ? SecurityAlgorithms.HmacSha384 : SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [Fact]
    public async Task Register_creates_user_and_profile_with_safe_response()
    {
        using var factory=new AuthApiFactory();using var client=factory.Client();var password=Password();
        var response=await client.PostAsJsonAsync("/api/v1/auth/register",Registration(" Test@CodeRank.test ",password),Token);
        Assert.Equal(HttpStatusCode.Created,response.StatusCode);
        Assert.Equal("/api/v1/users/me",response.Headers.Location?.ToString());
        var body=await response.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("password",body,StringComparison.OrdinalIgnoreCase);
        var dto=JsonSerializer.Deserialize<UserResponse>(body,new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("Test@CodeRank.test",dto.Email);Assert.Equal("User",dto.Role);
        Assert.Equal("Test",dto.FirstName);Assert.Equal("Student",dto.LastName);
        await using var db=factory.Context();var user=await db.Users.Include(u=>u.UserProfile).SingleAsync(Token);
        Assert.Equal("TEST@CODERANK.TEST",user.NormalizedEmail);Assert.True(user.IsActive);
        Assert.NotEqual(password,user.PasswordHash);
        Assert.Equal(PasswordVerificationResult.Success,new PasswordHasher<User>().VerifyHashedPassword(user,user.PasswordHash,password));
        Assert.Equal(user.Id,user.UserProfile!.UserId);
    }
    [Fact]
    public async Task Duplicate_email_ignores_case_and_outer_spaces()
    {
        using var factory=new AuthApiFactory();using var client=factory.Client();var password=Password();
        Assert.Equal(HttpStatusCode.Created,(await client.PostAsJsonAsync("/api/v1/auth/register",Registration("test@coderank.test",password),Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync("/api/v1/auth/register",Registration(" TEST@CODERANK.TEST ",password),Token)).StatusCode);
        await using var db=factory.Context();Assert.Equal(1,await db.Users.CountAsync(Token));Assert.Equal(1,await db.UserProfiles.CountAsync(Token));
    }
    [Fact]
    public async Task Unique_index_insert_race_returns_409_and_preserves_winning_record()
    {
        using var factory=new AuthApiFactory();factory.Interceptor=new InsertRace(()=>factory.Context());using var client=factory.Client();
        var response=await client.PostAsJsonAsync("/api/v1/auth/register",Registration("race@coderank.test",Password()),Token);
        Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);
        await using var db=factory.Context();Assert.Equal(1,await db.Users.CountAsync(Token));Assert.Equal("Winner",(await db.UserProfiles.SingleAsync(Token)).LastName);
    }
    [Theory]
    [InlineData("email")][InlineData("shortPassword")][InlineData("longPassword")]
    [InlineData("firstName")][InlineData("lastName")][InlineData("longName")][InlineData("longEmail")]
    public async Task Invalid_registration_is_400_without_writes(string variant)
    {
        using var factory=new AuthApiFactory();using var client=factory.Client();
        var request=new Dictionary<string,object?> { ["email"]="test@coderank.test",["password"]=Password(),["firstName"]="Test",["lastName"]="Student" };
        switch(variant) {
            case "email":request["email"]="invalid-email";break;
            case "shortPassword":request["password"]="x";break;
            case "longPassword":request["password"]=new string('x',129);break;
            case "firstName":request["firstName"]="   ";break;
            case "lastName":request.Remove("lastName");break;
            case "longName":request["firstName"]=new string('x',101);break;
            case "longEmail":request["email"]=new string('x',250)+"@coderank.test";break;
        }
        var response=await client.PostAsJsonAsync("/api/v1/auth/register",request,Token);
        Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);
        await using var db=factory.Context();Assert.Equal(0,await db.Users.CountAsync(Token));Assert.Equal(0,await db.UserProfiles.CountAsync(Token));
    }
    [Fact]
    public async Task Client_cannot_self_assign_admin()
    {
        using var factory=new AuthApiFactory();using var client=factory.Client();
        var response=await client.PostAsJsonAsync("/api/v1/auth/register",new { email="admin-attempt@coderank.test",password=Password(),firstName="Test",lastName="Student",role="Admin" },Token);
        Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);
        await using var db=factory.Context();Assert.Equal(0,await db.Users.CountAsync(Token));
    }
    [Fact]
    public async Task Profile_failure_rolls_back_user_and_returns_safe_500()
    {
        using var factory=new AuthApiFactory();factory.Interceptor=new FailOnProfileInsert();using var client=factory.Client();
        var response=await client.PostAsJsonAsync("/api/v1/auth/register",Registration("rollback@coderank.test",Password()),Token);
        Assert.Equal(HttpStatusCode.InternalServerError,response.StatusCode);
        var body=await response.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("Private database",body);Assert.DoesNotContain("INSERT",body);Assert.DoesNotContain("Exception",body);
        await using var db=factory.Context();Assert.Equal(0,await db.Users.CountAsync(Token));Assert.Equal(0,await db.UserProfiles.CountAsync(Token));
    }
    [Fact]
    public async Task Login_verifies_password_and_issues_signed_token_with_exact_expiry()
    {
        using var factory=new AuthApiFactory();using var client=factory.Client();var password=Password();var user=await ExistingAsync(factory,password);
        var response=await client.PostAsJsonAsync("/api/v1/auth/login",new { email=" EXISTING@CODERANK.TEST ",password },Token);
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var dto=(await response.Content.ReadFromJsonAsync<LoginResponse>(Token))!;
        Assert.Equal("Bearer",dto.TokenType);Assert.True(dto.ExpiresAtUtc>DateTimeOffset.UtcNow);
        var jwt=new JwtSecurityTokenHandler().ReadJwtToken(dto.AccessToken);
        Assert.Equal(SecurityAlgorithms.HmacSha256,jwt.Header.Alg);
        Assert.Equal(user.Id.ToString(CultureInfo.InvariantCulture),jwt.Subject);
        Assert.Equal(dto.ExpiresAtUtc.UtcDateTime,jwt.ValidTo);
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",dto.AccessToken);
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/api/v1/users/me",Token)).StatusCode);
    }
    [Theory]
    [InlineData("wrong")][InlineData("unknown")][InlineData("inactive")][InlineData("badHash")]
    public async Task Failed_login_always_returns_generic_401(string variant)
    {
        using var factory=new AuthApiFactory();using var client=factory.Client();var password=Password();var user=await ExistingAsync(factory,password,variant!="inactive");
        if(variant=="badHash") { await using var db=factory.Context();var row=await db.Users.SingleAsync(Token);row.PasswordHash="invalid-format";await db.SaveChangesAsync(Token); }
        var response=await client.PostAsJsonAsync("/api/v1/auth/login",new { email=variant=="unknown"?"absent@coderank.test":user.Email,password=variant=="wrong"?Password():password },Token);
        Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode);
        var body=JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Equal("Invalid credentials.",body.RootElement.GetProperty("title").GetString());
        Assert.False(body.RootElement.TryGetProperty("accessToken",out _));
    }
    [Theory]
    [InlineData("email")][InlineData("password")]
    public async Task Invalid_login_is_400(string variant)
    {
        using var factory=new AuthApiFactory();using var client=factory.Client();
        var response=await client.PostAsJsonAsync("/api/v1/auth/login",new { email=variant=="email"?"bad":"test@coderank.test",password=variant=="password"?"":Password() },Token);
        Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);
    }
    [Fact]
    public async Task Me_returns_fresh_profile_and_role_without_password_hash()
    {
        using var factory=new AuthApiFactory();using var client=factory.Client();var user=await ExistingAsync(factory,Password());
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",Signed(factory,user.Id));
        await using(var db=factory.Context()) { var current=await db.Users.Include(u=>u.UserProfile).SingleAsync(Token);current.Role="Admin";current.UserProfile!.FirstName="Updated";await db.SaveChangesAsync(Token); }
        var response=await client.GetAsync("/api/v1/users/me",Token);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var body=await response.Content.ReadAsStringAsync(Token);Assert.DoesNotContain("password",body,StringComparison.OrdinalIgnoreCase);
        var dto=JsonSerializer.Deserialize<UserResponse>(body,new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(user.Id,dto.Id);Assert.Equal("Updated",dto.FirstName);Assert.Equal("Admin",dto.Role);
    }
    [Theory]
    [InlineData("missing")][InlineData("tampered")][InlineData("expired")][InlineData("key")]
    [InlineData("issuer")][InlineData("audience")][InlineData("unsigned")][InlineData("subject")]
    [InlineData("algorithm")]
    public async Task Me_rejects_invalid_tokens(string variant)
    {
        using var factory=new AuthApiFactory();using var client=factory.Client();var user=await ExistingAsync(factory,Password());
        if(variant!="missing") {
            var token=variant=="tampered"?"invalid.jwt.signature":Signed(factory,user.Id,variant);
            client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
        }
        var response=await client.GetAsync("/api/v1/users/me",Token);Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode);
        var challenge=response.Headers.WwwAuthenticate.ToString();Assert.DoesNotContain("error_description",challenge);Assert.DoesNotContain("IDX",challenge);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Me_rejects_deleted_or_deactivated_users(bool deleted)
    {
        using var factory=new AuthApiFactory();using var client=factory.Client();var user=await ExistingAsync(factory,Password());
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",Signed(factory,user.Id));
        await using(var db=factory.Context()) {var row=await db.Users.SingleAsync(Token);if(deleted)db.Users.Remove(row);else row.IsActive=false;await db.SaveChangesAsync(Token);}
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/v1/users/me",Token)).StatusCode);
    }
    [Fact]
    public async Task Missing_optional_profile_is_returned_as_null()
    {
        using var factory=new AuthApiFactory();using var client=factory.Client();var user=await ExistingAsync(factory,Password());
        await using(var db=factory.Context()) {db.UserProfiles.Remove(await db.UserProfiles.SingleAsync(Token));await db.SaveChangesAsync(Token);}
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",Signed(factory,user.Id));
        var response=await client.GetFromJsonAsync<UserResponse>("/api/v1/users/me",Token);
        Assert.NotNull(response);Assert.Null(response.FirstName);Assert.Null(response.LastName);
    }
}

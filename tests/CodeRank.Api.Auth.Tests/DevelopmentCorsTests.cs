using System.Net;
using Xunit;

namespace CodeRank.Api.Auth.Tests;

public sealed class DevelopmentCorsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Development", "http://localhost:5173", true)]
    [InlineData("Development", "http://127.0.0.1:5173", false)]
    [InlineData("Development", "http://localhost:5174", false)]
    [InlineData("Development", "https://localhost:5173", false)]
    [InlineData("Development", "https://untrusted.example", false)]
    [InlineData("Production", "http://localhost:5173", false)]
    [InlineData("Testing", "http://localhost:5173", false)]
    public async Task Only_the_development_frontend_origin_receives_cors_headers(
        string environment, string origin, bool allowed)
    {
        using var factory = new AuthApiFactory(environment);
        using var client = factory.Client();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/users/me");
        request.Headers.Add("Origin", origin);
        using var response = await client.SendAsync(request, Token);

        // CORS must also work on an unauthenticated response, without bypassing authorization.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(allowed, response.Headers.Contains("Access-Control-Allow-Origin"));
        if (allowed)
        {
            Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        }
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Theory]
    [InlineData("POST", "content-type")]
    [InlineData("GET", "authorization")]
    [InlineData("PUT", "content-type,authorization")]
    [InlineData("DELETE", "authorization")]
    public async Task Development_preflight_accepts_required_methods_and_headers(string method, string header)
    {
        using var factory = new AuthApiFactory("Development");
        using var client = factory.Client();
        using var request = new HttpRequestMessage(HttpMethod.Options,
            method is "PUT" or "DELETE" ? "/api/v1/problems/1"
                : method == "POST" ? "/api/v1/auth/login" : "/api/v1/users/me");
        request.Headers.Add("Origin", "http://localhost:5173");
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", header);
        using var response = await client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("http://localhost:5173", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains(method, string.Join(",", response.Headers.GetValues("Access-Control-Allow-Methods")));
        foreach (var name in header.Split(','))
            Assert.Contains(name, string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers")),
                StringComparison.OrdinalIgnoreCase);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Theory]
    [InlineData("Development", "http://localhost:5174", "PUT")]
    [InlineData("Development", "http://localhost:5174", "DELETE")]
    [InlineData("Production", "http://localhost:5173", "PUT")]
    [InlineData("Production", "http://localhost:5173", "DELETE")]
    [InlineData("Testing", "http://localhost:5173", "PUT")]
    [InlineData("Testing", "http://localhost:5173", "DELETE")]
    public async Task New_methods_do_not_enable_other_origins_or_environments(string environment, string origin, string method)
    {
        using var factory = new AuthApiFactory(environment);
        using var client = factory.Client();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/problems/1");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", "authorization");
        using var response = await client.SendAsync(request, Token);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }
}

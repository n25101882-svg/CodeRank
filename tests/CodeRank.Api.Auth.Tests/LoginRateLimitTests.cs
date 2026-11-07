using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace CodeRank.Api.Auth.Tests;

public sealed class LoginRateLimitTests
{
    private const int PermitLimit = 10;
    private const string LoginPath = "/api/v1/auth/login";
    private const string Email = "rate-limit@coderank.test";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Login_rejects_excess_attempts_and_recovers_at_the_window_boundary()
    {
        var clock = new LoginClock();
        using var factory = new AuthApiFactory();
        using var host = WithClock(factory, clock);
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var password = Guid.NewGuid().ToString();
        using var registered = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email = Email, password, firstName = "Тест", lastName = "Учасник" }, Token);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        var wrongPassword = Guid.NewGuid().ToString();
        for (var attempt = 0; attempt < PermitLimit; attempt++)
        {
            using var denied = await client.PostAsJsonAsync(LoginPath, new { email = Email, password = wrongPassword }, Token);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }

        using var limited = await client.PostAsJsonAsync(LoginPath, new { email = Email, password }, Token);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(60), limited.Headers.RetryAfter?.Delta);
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType?.MediaType);
        var details = await limited.Content.ReadFromJsonAsync<ProblemDetails>(Token);
        Assert.Equal("Too many login attempts. Please try again later.", details?.Title);

        using var olympiads = await client.GetAsync("/api/v1/olympiads", Token);
        Assert.Equal(HttpStatusCode.OK, olympiads.StatusCode);
        using var anotherRegistration = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = "rate-limit-other@coderank.test", password, firstName = "Тест", lastName = "Учасник"
        }, Token);
        Assert.Equal(HttpStatusCode.Created, anotherRegistration.StatusCode);

        clock.Advance(TimeSpan.FromSeconds(59));
        using var stillLimited = await client.PostAsJsonAsync(LoginPath,
            new { email = "unknown@coderank.test", password = wrongPassword }, Token);
        Assert.Equal(HttpStatusCode.TooManyRequests, stillLimited.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), stillLimited.Headers.RetryAfter?.Delta);

        clock.Advance(TimeSpan.FromSeconds(1));
        using var recovered = await client.PostAsJsonAsync(LoginPath, new { email = Email, password }, Token);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Fact]
    public async Task Direct_loopback_ignores_spoofed_headers_and_shares_one_budget()
    {
        using var factory = new AuthApiFactory();
        using var host = WithClock(factory, new LoginClock());
        for (var i = 0; i < PermitLimit; i++)
        {
            var peer = i % 2 == 0 ? IPAddress.Loopback : IPAddress.Loopback.MapToIPv6();
            Assert.Equal(401, await AttemptAsync(host.Server, peer, $"203.0.113.{i + 1}"));
        }

        Assert.Equal(429, await AttemptAsync(host.Server, IPAddress.IPv6Loopback, "203.0.113.50"));
        Assert.Equal(401, await AttemptAsync(host.Server, IPAddress.Parse("198.51.100.21")));
    }

    [Fact]
    public async Task Only_the_configured_proxy_can_supply_the_last_forwarded_client_address()
    {
        var proxy = IPAddress.Parse("192.0.2.10");
        var client = IPAddress.Parse("198.51.100.20");
        using var factory = new AuthApiFactory();
        using var host = WithClock(factory, new LoginClock(), proxy);
        for (var i = 0; i < PermitLimit; i++)
            Assert.Equal(401, await AttemptAsync(host.Server, proxy, $"203.0.113.{i + 1}, {client}"));

        Assert.Equal(429, await AttemptAsync(host.Server, proxy, $"203.0.113.50, {client}"));
        Assert.Equal(429, await AttemptAsync(host.Server, client.MapToIPv6(), "198.51.100.21"));
        Assert.Equal(401, await AttemptAsync(host.Server, proxy, "198.51.100.21"));
        Assert.Equal(401, await AttemptAsync(host.Server, null, client.ToString()));
    }

    private static WebApplicationFactory<Program> WithClock(AuthApiFactory factory, LoginClock clock, IPAddress? proxy = null)
    {
        return factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            });
            if (proxy is not null)
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["ReverseProxy:KnownProxies:0"] = proxy.ToString() }));
        });
    }

    private static async Task<int> AttemptAsync(TestServer server, IPAddress? peer, string? forwardedFor = null)
    {
        var request = new { email = "unknown@coderank.test", password = Guid.NewGuid().ToString() };
        using var body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(request));
        var result = await server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = peer;
            context.Request.Method = HttpMethods.Post;
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("localhost");
            context.Request.Path = LoginPath;
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = body.Length;
            context.Request.Body = body;
            if (forwardedFor is not null)
                context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }, Token);
        return result.Response.StatusCode;
    }

    private sealed class LoginClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref timestamp, elapsed.Ticks);
    }
}

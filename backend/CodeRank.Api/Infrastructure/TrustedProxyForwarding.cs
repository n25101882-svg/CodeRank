using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace CodeRank.Api.Infrastructure;

internal static class TrustedProxyForwarding
{
    public static void UseTrustedProxyForwarding(this WebApplication app)
    {
        var proxies = app.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [];
        if (proxies.Length == 0) return;

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var proxy in proxies)
        {
            if (!IPAddress.TryParse(proxy, out var address))
                throw new InvalidOperationException("ReverseProxy:KnownProxies requires explicit proxy IP addresses.");
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
                throw new InvalidOperationException("ReverseProxy:KnownProxies cannot contain wildcard addresses.");
            options.KnownProxies.Add(address);
        }

        // Without a TCP peer address there is no trusted source to match against the allowlist.
        app.UseWhen(context => context.Connection.RemoteIpAddress is not null,
            branch => branch.UseForwardedHeaders(options));
    }
}

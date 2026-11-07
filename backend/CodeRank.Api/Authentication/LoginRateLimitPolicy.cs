using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CodeRank.Api.Authentication;

internal sealed class LoginRateLimitPolicy(TimeProvider clock) : IRateLimiterPolicy<string>
{
    public const string Name = "login";
    private const int PermitLimit = 10;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public RateLimitPartition<string> GetPartition(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address?.IsIPv4MappedToIPv6 == true)
            address = address.MapToIPv4();
        var source = address is null ? "unknown"
            : IPAddress.IsLoopback(address) ? "loopback" : address.ToString();
        return RateLimitPartition.Get(source, _ => new WindowLimiter(clock));
    }

    public Func<OnRejectedContext, CancellationToken, ValueTask> OnRejected => RejectAsync;

    private static ValueTask RejectAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var response = context.HttpContext.Response;
        response.StatusCode = StatusCodes.Status429TooManyRequests;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delay))
            response.Headers.RetryAfter = Math.Ceiling(delay.TotalSeconds).ToString(CultureInfo.InvariantCulture);

        return new ValueTask(response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Too many login attempts. Please try again later."
        }, options: null, contentType: "application/problem+json", cancellationToken: cancellationToken));
    }

    // ASP.NET Core manages partitions and idle eviction. The monotonic clock lets tests advance the real policy.
    private sealed class WindowLimiter(TimeProvider clock) : RateLimiter
    {
        private static readonly RateLimitLease Acquired = new WindowLease(null);
        private readonly object gate = new();
        private long windowStart = clock.GetTimestamp();
        private int available = PermitLimit;

        public override TimeSpan? IdleDuration
        {
            get
            {
                lock (gate)
                {
                    var elapsed = clock.GetElapsedTime(windowStart);
                    return elapsed >= Window ? elapsed - Window : null;
                }
            }
        }

        public override RateLimiterStatistics? GetStatistics() => null;

        protected override RateLimitLease AttemptAcquireCore(int permitCount)
        {
            lock (gate)
            {
                var now = clock.GetTimestamp();
                var elapsed = clock.GetElapsedTime(windowStart, now);
                if (elapsed >= Window)
                {
                    windowStart = now;
                    available = PermitLimit;
                    elapsed = TimeSpan.Zero;
                }

                if (available == 0 || permitCount > available)
                    return new WindowLease(Window - elapsed);
                available -= permitCount;
                return Acquired;
            }
        }

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
            => ValueTask.FromResult(AttemptAcquireCore(permitCount));
    }

    private sealed class WindowLease(TimeSpan? retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => retryAfter is null;
        public override IEnumerable<string> MetadataNames => IsAcquired ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = metadataName == MetadataName.RetryAfter.Name ? retryAfter : null;
            return metadata is not null;
        }
    }
}

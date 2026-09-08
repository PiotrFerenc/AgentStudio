using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>Exercises the AuthRateLimiting.GetPartition policy (Program.cs) directly against
/// System.Threading.RateLimiting, rather than through an HTTP integration test: TestServer's
/// in-memory transport never sets Connection.RemoteIpAddress, so an HTTP-level test could only
/// ever hit the TraceIdentifier fallback branch, never the real per-IP behavior.</summary>
public class AuthRateLimitingTests
{
    private static DefaultHttpContext ContextForIp(string ip)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        return ctx;
    }

    [Fact]
    public void Attempt_beyond_permit_limit_from_same_ip_is_rejected()
    {
        using var limiter = PartitionedRateLimiter.Create<HttpContext, string>(AuthRateLimiting.GetPartition);

        for (var i = 0; i < AuthRateLimiting.PermitLimit; i++)
            Assert.True(limiter.AttemptAcquire(ContextForIp("10.0.0.1")).IsAcquired);

        Assert.False(limiter.AttemptAcquire(ContextForIp("10.0.0.1")).IsAcquired);
    }

    [Fact]
    public void Different_ip_has_its_own_budget()
    {
        using var limiter = PartitionedRateLimiter.Create<HttpContext, string>(AuthRateLimiting.GetPartition);

        for (var i = 0; i < AuthRateLimiting.PermitLimit; i++)
            Assert.True(limiter.AttemptAcquire(ContextForIp("10.0.0.1")).IsAcquired);

        Assert.True(limiter.AttemptAcquire(ContextForIp("10.0.0.2")).IsAcquired);
    }

    [Fact]
    public void Missing_remote_ip_falls_back_to_per_request_identity_not_a_shared_bucket()
    {
        using var limiter = PartitionedRateLimiter.Create<HttpContext, string>(AuthRateLimiting.GetPartition);

        // Simulates a transport that can't report a real IP (e.g. an in-memory test server):
        // requests must not all pile into one shared bucket, or one anonymous client would
        // exhaust the limit for every other client behind the same unknown-IP fallback.
        for (var i = 0; i < AuthRateLimiting.PermitLimit + 3; i++)
            Assert.True(limiter.AttemptAcquire(new DefaultHttpContext()).IsAcquired);
    }
}

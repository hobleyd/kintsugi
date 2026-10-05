using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Kintsugi.WebApi.Security;

/// <summary>
/// The rate limit on <c>POST /api/host/enroll</c>, the one agent route that is reachable with
/// neither a client certificate nor a session.
/// </summary>
/// <remarks>
/// <para>
/// Enrollment is protected by the shared token alone (see <c>EnrollAgentCommandHandler</c>), and
/// every accepted call has the fleet CA issue a certificate. Nothing throttled it, so a single
/// source could try tokens, or mint certificates, as fast as the server would answer. This bounds
/// both per source address. The figures are generous for anything legitimate: a host enrolls once,
/// and a fleet re-enrolling after the CA is regenerated does so from many addresses, each spread
/// across the hour by its own check-in minute.
/// </para>
/// <para>
/// Partitioned by the client address as the forwarded-headers middleware has already resolved it
/// — <c>Program.cs</c> runs <c>UseForwardedHeaders</c> first, and nginx appends the real peer to
/// <c>X-Forwarded-For</c>, so this is the address nginx saw rather than nginx's own. A deployment
/// with something in front of nginx that does not forward the address collapses every caller into
/// one partition; the limit then still holds, only fleet-wide, which is the safer way to be wrong.
/// </para>
/// </remarks>
public static class EnrollmentRateLimit
{
    public const string PolicyName = "enrollment";

    /// <summary>Accepted enrollment attempts per source address per window.</summary>
    public const int PermitLimit = 10;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static void Configure(RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy(PolicyName, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                PartitionKey(httpContext.Connection.RemoteIpAddress?.ToString()),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = PermitLimit,
                    Window = Window,
                    // Rejected outright rather than queued: an agent that is told 429 retries on
                    // its own schedule, and a queued request would hold a connection open for
                    // nothing.
                    QueueLimit = 0,
                }));
    }

    /// <summary>Pulled out so the fallback for a request with no resolvable address is a tested
    /// value rather than an accident of string formatting.</summary>
    public static string PartitionKey(string? remoteAddress) =>
        string.IsNullOrWhiteSpace(remoteAddress) ? "unknown" : remoteAddress;
}

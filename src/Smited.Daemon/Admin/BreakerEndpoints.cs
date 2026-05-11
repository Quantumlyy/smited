using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Smited.Daemon.Triggering;

namespace Smited.Daemon.Admin;

/// <summary>
/// Maps the breaker REST endpoints onto the admin pipeline. Mounted by
/// <c>Program.cs</c>'s <c>MapWhen</c> branch on the admin port (default
/// 7779) alongside the Blazor hub and fallback page.
/// </summary>
/// <remarks>
/// <para>
/// The admin UI itself uses direct service injection
/// (<see cref="IBreakerService"/> / <see cref="IBreakerChallengeService"/>)
/// rather than going through these HTTP endpoints. The endpoints exist
/// for external clients — future smited-watch tooling, ops scripts —
/// that want to drive the breaker without depending on the in-process
/// Blazor UI or a wire-schema bump.
/// </para>
/// <para>
/// Endpoints:
/// <list type="bullet">
///   <item><c>GET /admin/breaker</c> — current state.</item>
///   <item><c>POST /admin/breaker/rearm/challenge</c> — generate a
///   single-use challenge.</item>
///   <item><c>POST /admin/breaker/rearm</c> — verify the challenge and
///   re-arm.</item>
/// </list>
/// </para>
/// </remarks>
internal static class BreakerEndpoints
{
    public static IEndpointRouteBuilder MapBreaker(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/admin/breaker", (IBreakerService breaker) =>
        {
            return Results.Ok(new
            {
                tripped = breaker.IsTripped,
                trippedAt = breaker.TrippedAt,
                reason = breaker.TripReason,
            });
        });

        endpoints.MapPost("/admin/breaker/rearm/challenge",
            async (IBreakerChallengeService challenges, CancellationToken ct) =>
            {
                var c = await challenges.GenerateAsync(ct);
                return Results.Ok(new
                {
                    challenge = c.Token,
                    expiresAt = c.ExpiresAt,
                });
            });

        endpoints.MapPost("/admin/breaker/rearm",
            async (
                RearmRequest? req,
                IBreakerChallengeService challenges,
                IBreakerService breaker,
                CancellationToken ct) =>
            {
                if (req is null || string.IsNullOrEmpty(req.Challenge))
                {
                    return Results.BadRequest(new { error = "challenge required" });
                }
                var ok = await challenges.VerifyAndConsumeAsync(req.Challenge, ct);
                if (!ok)
                {
                    return Results.BadRequest(new
                    {
                        error = "invalid or expired challenge — generate a new one",
                    });
                }
                breaker.Rearm();
                return Results.Ok(new { rearmed = true });
            });

        return endpoints;
    }

    public sealed record RearmRequest(string Challenge);
}

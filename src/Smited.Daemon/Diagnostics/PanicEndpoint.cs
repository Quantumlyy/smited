using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Smited.Daemon.Triggering;

namespace Smited.Daemon.Diagnostics;

/// <summary>
/// Maps the <c>/panic</c> emergency-stop endpoint. Cancels every active
/// sensation across every backend. POST-only with no body — GET is
/// rejected so cross-site triggers via <c>&lt;img src&gt;</c> tags can't
/// drive the endpoint accidentally. Returns immediately with a count of
/// stopped sensations.
///
/// Listens on its own Kestrel port (HTTP/1.1) so a wedged gRPC pipeline
/// can't take this endpoint down with it. Bypasses the protovalidate
/// interceptor (gRPC-only) and any backend-bootstrap gating: as long as
/// the host is up and <see cref="SmitedActionService"/> exists, the
/// endpoint works — at worst it stops zero sensations because none are
/// active.
///
/// The stop call uses the application-lifetime cancellation token, NOT
/// <c>HttpContext.RequestAborted</c>. A panic must run to completion
/// even if the panicking client (Streamdeck Companion, curl) drops the
/// connection mid-flight; otherwise a real backend that honors
/// cancellation could abort the emergency stop.
///
/// CRITICAL-level audit logging and history recording live in the
/// <see cref="SmitedActionService"/> facade so admin-fired and panic-HTTP-
/// fired panics produce identical postmortem signals.
/// </summary>
internal static class PanicEndpoint
{
    public static IEndpointRouteBuilder MapPanic(this IEndpointRouteBuilder app)
    {
        var handler = async (
            HttpContext ctx,
            SmitedActionService actions,
            IHostApplicationLifetime lifetime) =>
        {
            var peer = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var userAgent = ctx.Request.Headers.UserAgent.ToString();

            int stopped;
            try
            {
                stopped = await actions.PanicAsync(
                    TriggerSource.PanicHttp, peer, userAgent, lifetime.ApplicationStopping);
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    ok = false,
                    error = ex.Message,
                });
                return;
            }

            await ctx.Response.WriteAsJsonAsync(new
            {
                ok = true,
                stopped,
            });
        };

        app.MapPost("/panic", handler)
            .WithName("PanicStop")
            .WithDisplayName("Emergency stop");

        return app;
    }
}

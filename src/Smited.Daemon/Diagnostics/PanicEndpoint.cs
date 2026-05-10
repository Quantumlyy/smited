using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.History;
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
/// the host is up and <see cref="TriggerCoordinator"/> exists, the
/// endpoint works — at worst it stops zero sensations because none are
/// active.
///
/// The stop call uses the application-lifetime cancellation token, NOT
/// <c>HttpContext.RequestAborted</c>. A panic must run to completion
/// even if the panicking client (Streamdeck Companion, curl) drops the
/// connection mid-flight; otherwise a real backend that honors
/// cancellation could abort the emergency stop.
/// </summary>
internal static class PanicEndpoint
{
    public static IEndpointRouteBuilder MapPanic(this IEndpointRouteBuilder app)
    {
        var handler = async (
            HttpContext ctx,
            TriggerCoordinator coordinator,
            IHistoryRecorder history,
            TimeProvider time,
            IHostApplicationLifetime lifetime,
            ILogger<PanicMarker> log) =>
        {
            var peer = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var userAgent = ctx.Request.Headers.UserAgent.ToString();
            var timestamp = time.GetUtcNow();

            log.LogCritical(
                "PANIC stop requested from {Peer} (UA: {UserAgent})",
                peer, userAgent);

            int stopped;
            try
            {
                stopped = await coordinator.StopAsync(
                    new BackendStopRequest(SensationId: null, All: true),
                    lifetime.ApplicationStopping);
            }
            catch (Exception ex)
            {
                // Even if the coordinator throws, log loudly and tell the
                // caller. Don't let a panic invocation silently fail.
                log.LogCritical(ex,
                    "PANIC stop FAILED from {Peer}; coordinator threw",
                    peer);
                _ = history.RecordPanicAsync(new PanicRecord
                {
                    Timestamp = timestamp,
                    Peer = peer,
                    UserAgent = userAgent,
                    Ok = false,
                    StoppedCount = 0,
                    Error = ex.Message,
                });
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    ok = false,
                    error = ex.Message,
                });
                return;
            }

            log.LogCritical(
                "PANIC stop completed from {Peer}, {StoppedCount} sensation(s) cancelled",
                peer, stopped);

            // History: one panic row plus a paired stop row.
            _ = history.RecordPanicAsync(new PanicRecord
            {
                Timestamp = timestamp,
                Peer = peer,
                UserAgent = userAgent,
                Ok = true,
                StoppedCount = stopped,
            });
            _ = history.RecordStopAsync(new StopRecord
            {
                Timestamp = timestamp,
                Source = "panic",
                All = true,
                StoppedCount = stopped,
            });

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

    /// <summary>
    /// Marker type so PANIC log entries categorise under
    /// <c>Smited.Daemon.Diagnostics.PanicMarker</c> and stay trivially
    /// filterable in any sink.
    /// </summary>
    internal sealed class PanicMarker { }
}

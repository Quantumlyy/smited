using Microsoft.Extensions.Logging;
using Smited.Daemon.Admin.Services;
using Smited.Daemon.Backends;

namespace Smited.Daemon.Admin.Components.Pages;

/// <summary>
/// Pure view-model builder for the body map. Takes the registered
/// backends, the latest activity snapshot, a clock, and the optional
/// backend filter; returns front- and back-pane <see cref="RenderedZone"/>
/// lists ready for the SVG renderer to position.
/// </summary>
/// <remarks>
/// Lives outside <c>BodySilhouette.razor</c> so the projection rules
/// — frame filtering, heatmap-fade math, group-vs-leaf handling,
/// front/back-pane assignment — are directly unit-testable without
/// standing up a Blazor render harness.
/// </remarks>
internal static class BodyMapRenderModel
{
    /// <summary>Heatmap fade window. Linear from 1.0 at LastFiredAt to 0
    /// at LastFiredAt + this duration.</summary>
    public const double FadeWindowSeconds = 3.0;

    /// <summary>
    /// Build front- and back-pane <see cref="RenderedZone"/> lists for
    /// the current snapshot. The renderer calls this on every fade-tick
    /// re-render so the per-zone <see cref="RenderedZone.HeatLevel"/> is
    /// always computed against the current clock — that's what makes the
    /// 3-second fade actually advance between events.
    /// </summary>
    /// <param name="backends">All registered backends.</param>
    /// <param name="activity">Latest snapshot from <see cref="BodyMapPageState.Snapshot"/>.</param>
    /// <param name="bodyMap">Optional concrete state for forbidden-zone lookup.</param>
    /// <param name="now">Current clock — used for fade decay.</param>
    /// <param name="backendFilter">If non-empty, include only this backend id.</param>
    /// <param name="log">Optional logger for skipped-zone diagnostics.</param>
    public static (IReadOnlyList<RenderedZone> Front, IReadOnlyList<RenderedZone> Back) Build(
        IEnumerable<IHapticBackend> backends,
        IReadOnlyDictionary<(string BackendId, string ZoneId), ZoneActivity> activity,
        global::Smited.Daemon.BodyMap.BodyMapState? bodyMap,
        DateTimeOffset now,
        string? backendFilter = null,
        ILogger? log = null)
    {
        var front = new List<RenderedZone>();
        var back = new List<RenderedZone>();

        foreach (var backend in backends)
        {
            if (!string.IsNullOrEmpty(backendFilter)
             && !string.Equals(backend.Id, backendFilter, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var zone in backend.Zones.Zones)
            {
                if (zone.Position is null)
                {
                    log?.LogDebug("Zone {BackendId}/{ZoneId} has no PositionHint; skipping on body map",
                        backend.Id, zone.Id);
                    continue;
                }

                // PiShock advertises Frame="device" at (0.5, 0.5, 0.5) —
                // device-local coords meaning "centered on whatever the
                // hardware is strapped to." Projecting that as body
                // coords would draw every PiShock dot in the torso
                // center regardless of where the operator placed the
                // device on the user. The body-map descriptor is the
                // source of truth for placement; until the renderer
                // knows how to project a region into body coords (which
                // would need region-center metadata that doesn't exist
                // today), gate strictly on the body frame.
                if (!string.Equals(zone.Position.Frame, "body", StringComparison.Ordinal))
                {
                    log?.LogDebug("Zone {BackendId}/{ZoneId} uses non-body frame {Frame}; skipping on body map",
                        backend.Id, zone.Id, zone.Position.Frame);
                    continue;
                }

                activity.TryGetValue((backend.Id, zone.Id), out var act);

                var rendered = new RenderedZone(
                    BackendId: backend.Id,
                    BackendKind: backend.Kind,
                    BackendDisplayName: backend.DisplayName,
                    ZoneId: zone.Id,
                    ZoneDisplayName: string.IsNullOrEmpty(zone.DisplayName) ? zone.Id : zone.DisplayName,
                    Position: zone.Position,
                    IsActive: act?.IsActive ?? false,
                    HeatLevel: ComputeHeatLevel(act, now),
                    IsForbidden: IsZoneForbidden(bodyMap, backend, zone.Id));

                // Z=0 is front, Z=1 is back; Z=0.5 (limbs like arms) is
                // the side of the body. Render Z<=0.5 on the front pane
                // — limbs are visible face-on, and grouping them with
                // the front view reads better than tucking them behind.
                if (zone.Position.Z <= 0.5f) front.Add(rendered);
                else back.Add(rendered);
            }
        }

        return (front, back);
    }

    /// <summary>
    /// Linear fade: 1.0 at <see cref="ZoneActivity.LastFiredAt"/>, 0.0 at
    /// <see cref="ZoneActivity.LastFiredAt"/> + <see cref="FadeWindowSeconds"/>.
    /// Active zones override to 1.0 regardless of age so the
    /// currently-firing state never reads as dim.
    /// </summary>
    public static double ComputeHeatLevel(ZoneActivity? activity, DateTimeOffset now)
    {
        if (activity is null) return 0;
        if (activity.IsActive) return 1.0;

        var elapsed = (now - activity.LastFiredAt).TotalSeconds;
        if (elapsed >= FadeWindowSeconds) return 0;
        return 1.0 - (elapsed / FadeWindowSeconds);
    }

    private static bool IsZoneForbidden(global::Smited.Daemon.BodyMap.BodyMapState? bodyMap, IHapticBackend backend, string zoneId)
    {
        if (bodyMap is null) return false;
        if (!bodyMap.ZoneRegions.TryGetValue(backend.Id, out var zoneMap)) return false;
        if (!zoneMap.TryGetValue(zoneId, out var region)) return false;
        return backend.ForbiddenRegions.Contains(region);
    }
}

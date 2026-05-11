using Smited.V1;

namespace Smited.Daemon.Admin.Components.Pages;

/// <summary>
/// View-model passed from <c>BodySilhouette</c> to <c>ZoneMarker</c> /
/// <c>ZoneTooltip</c>. Combines the zone descriptor with the runtime
/// state the renderer needs (active, heat-fade level, forbidden).
/// Public because Blazor's generated component classes expose
/// <c>[Parameter]</c> properties publicly; an internal record here
/// trips C# accessibility consistency.
/// </summary>
public sealed record RenderedZone(
    string BackendId,
    string BackendKind,
    string BackendDisplayName,
    string ZoneId,
    string ZoneDisplayName,
    PositionHint Position,
    bool IsActive,
    double HeatLevel,
    bool IsForbidden);

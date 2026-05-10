namespace Smited.Daemon.BodyMap;

/// <summary>
/// Declared physical placement of a single backend zone (or zone group,
/// expanded at validation time) on the user's body. Authored by the
/// user in configuration under <c>Smited:BodyMap:Placements</c>;
/// validated against the backend's manufacturer-mandated
/// <c>IHapticBackend.ForbiddenRegions</c> and smited's own conservative
/// defaults at startup.
/// </summary>
/// <remarks>
/// A single <see cref="Placement"/> entry covers one
/// (<see cref="BackendId"/>, <see cref="Region"/>) pair with one or
/// more zones. A backend whose zones span multiple regions appears as
/// multiple placement entries — one per region. This shape composes
/// cleanly with future per-placement metadata
/// (orientation, calibration overrides).
/// </remarks>
public sealed class Placement
{
    /// <summary>
    /// Backend the placement applies to. Must match a registered
    /// <c>BackendDescriptor.Id</c>; the validator reports an
    /// <c>UnknownBackend</c> error otherwise.
    /// </summary>
    public string BackendId { get; set; } = "";

    /// <summary>
    /// Zone IDs (or zone-group IDs) on the backend that occupy
    /// <see cref="Region"/>. Groups expand to their constituent zones
    /// using the backend's <c>ZoneTopology.Groups</c>; the validator
    /// reports an <c>UnknownZone</c> error if a referenced id is
    /// neither a leaf zone nor a group.
    /// </summary>
    public List<string> ZoneIds { get; set; } = new();

    /// <summary>
    /// Body region the listed zones occupy. A single zone may only
    /// appear in one placement; declaring it twice across overlapping
    /// regions surfaces as a configuration error.
    /// </summary>
    public BodyRegion Region { get; set; } = BodyRegion.Unspecified;

    /// <summary>
    /// Optional human-readable note for the user's own bookkeeping
    /// (e.g. "main vest", "wrist strap"). Has no effect on daemon
    /// behavior.
    /// </summary>
    public string? Description { get; set; }
}

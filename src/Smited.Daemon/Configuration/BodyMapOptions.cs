using Smited.Daemon.BodyMap;

namespace Smited.Daemon.Configuration;

/// <summary>
/// Configuration for the daemon-internal bodymap framework. Lives under
/// <c>Smited:BodyMap</c> in <c>appsettings.json</c>.
/// </summary>
/// <remarks>
/// Empty defaults mean "unmapped mode" — startup logs an INFO line
/// recommending configuration and every body-map check is a no-op. The
/// daemon does not infer placements from zone names.
/// </remarks>
public sealed class BodyMapOptions
{
    /// <summary>
    /// Declared placements. Each entry maps a backend's zones onto a
    /// <see cref="BodyRegion"/>; the validator runs at startup against
    /// the backend's manufacturer-mandated forbidden regions and
    /// smited's own defaults.
    /// </summary>
    public List<Placement> Placements { get; set; } = new();

    /// <summary>
    /// Behavior when multiple backends overlap on the same region.
    /// Default <see cref="OverlapPolicy.Warn"/>.
    /// </summary>
    public OverlapPolicy OverlapPolicy { get; set; } = OverlapPolicy.Warn;

    /// <summary>
    /// Regions the user explicitly opts out of smited's conservative
    /// default forbidden list (<c>SmitedDefaultForbiddenRegions</c>).
    /// Manufacturer-mandated forbidden regions remain non-overridable
    /// and ignore this list.
    /// </summary>
    public List<BodyRegion> AllowOverrideRegions { get; set; } = new();
}

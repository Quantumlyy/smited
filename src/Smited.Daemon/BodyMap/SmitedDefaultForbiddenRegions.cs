using System.Collections.Immutable;

namespace Smited.Daemon.BodyMap;

/// <summary>
/// Conservative defaults: regions smited treats as forbidden for every
/// backend out of the box. Distinct from a backend's
/// manufacturer-mandated <c>IHapticBackend.ForbiddenRegions</c> (which
/// is non-overridable). Users can opt out of any of these defaults by
/// adding the region to <see cref="Configuration.BodyMapOptions.AllowOverrideRegions"/>.
/// </summary>
internal static class SmitedDefaultForbiddenRegions
{
    /// <summary>
    /// Regions blocked by default for every backend. Users who know
    /// what they're doing — and have hardware whose manufacturer
    /// hasn't disclaimed the area — opt out via
    /// <c>BodyMapOptions.AllowOverrideRegions</c>.
    /// </summary>
    public static readonly IReadOnlySet<BodyRegion> Default = ImmutableHashSet.Create(
        BodyRegion.Face,
        BodyRegion.Throat,
        BodyRegion.Pelvis,
        BodyRegion.ChestOverHeart);
}

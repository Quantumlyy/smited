using System.Collections.Immutable;

namespace Smited.Daemon.BodyMap;

/// <summary>
/// Region containment relationships used by the bodymap validator.
/// Forbidden-region checks travel up the hierarchy: declaring a backend
/// zone in a sub-region (e.g. <see cref="BodyRegion.ChestOverHeart"/>)
/// also exposes that zone to forbidden-region rules attached to the
/// containing region (<see cref="BodyRegion.ChestFront"/>).
/// </summary>
/// <remarks>
/// The hierarchy is intentionally shallow. Today only the
/// <c>ChestOverHeart ⊂ ChestFront</c> edge matters; future additions
/// (pelvis sub-regions, arm-segment refinements) extend
/// <see cref="ContainingRegions(BodyRegion)"/> case-by-case.
/// </remarks>
internal static class RegionHierarchy
{
    /// <summary>
    /// Returns every region that contains the supplied region,
    /// including the region itself. The forbidden-region check uses
    /// this to walk upward: if any region in the returned set is in a
    /// forbidden list, the original placement is rejected.
    /// </summary>
    public static IReadOnlySet<BodyRegion> ContainingRegions(BodyRegion region) =>
        region switch
        {
            BodyRegion.ChestOverHeart =>
                ImmutableHashSet.Create(BodyRegion.ChestOverHeart, BodyRegion.ChestFront),
            _ =>
                ImmutableHashSet.Create(region),
        };
}

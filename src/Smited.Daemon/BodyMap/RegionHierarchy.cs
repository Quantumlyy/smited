namespace Smited.Daemon.BodyMap;

/// <summary>
/// Anatomical containment relationships between <see cref="BodyRegion"/>
/// values, exposed as a symmetric <see cref="Overlaps(BodyRegion, BodyRegion)"/>
/// predicate. Two regions overlap when they're the same region, when one
/// is a parent of the other, or when one is a child of the other —
/// regardless of which side of the call holds which role.
/// </summary>
/// <remarks>
/// <para>
/// The symmetric framing closes the cardiac-safety hole that an earlier
/// one-directional <c>ContainingRegions</c> walker had: a placement
/// declared in <see cref="BodyRegion.ChestFront"/> would bypass the
/// default-forbidden <see cref="BodyRegion.ChestOverHeart"/> sub-region
/// because the walker only travelled child→parent. <c>Overlaps</c>
/// answers the question the validator actually needs to ask — "do
/// these two regions intersect anatomically?" — without the caller
/// having to remember which is the broader claim.
/// </para>
/// <para>
/// The hierarchy is intentionally shallow. Today only the
/// <c>ChestOverHeart ⊂ ChestFront</c> edge exists; future additions
/// (pelvis sub-regions, arm-segment refinements) extend
/// <see cref="Contains(BodyRegion, BodyRegion)"/> case-by-case.
/// </para>
/// </remarks>
internal static class RegionHierarchy
{
    /// <summary>
    /// Returns <c>true</c> when regions <paramref name="a"/> and
    /// <paramref name="b"/> overlap anatomically — same region, parent
    /// of, or child of. Symmetric: <c>Overlaps(a, b) == Overlaps(b, a)</c>.
    /// </summary>
    public static bool Overlaps(BodyRegion a, BodyRegion b)
    {
        if (a == b) return true;
        if (Contains(a, b)) return true;
        if (Contains(b, a)) return true;
        return false;
    }

    /// <summary>
    /// True iff <paramref name="parent"/> contains <paramref name="child"/>
    /// in the anatomical hierarchy. Add new parent→child pairs here when
    /// sub-regions are introduced (e.g. <c>AbdomenLower → Pelvis</c>).
    /// </summary>
    private static bool Contains(BodyRegion parent, BodyRegion child) =>
        parent switch
        {
            BodyRegion.ChestFront => child == BodyRegion.ChestOverHeart,
            _ => false,
        };
}

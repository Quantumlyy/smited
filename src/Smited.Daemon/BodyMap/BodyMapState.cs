using System.Collections.Immutable;
using Smited.Daemon.Backends;

namespace Smited.Daemon.BodyMap;

/// <summary>
/// First overlap match found by <see cref="IBodyMapState.CheckOverlap"/>.
/// </summary>
/// <param name="Region">Region where the overlap occurs.</param>
/// <param name="OtherBackendId">A backend other than the trigger's that also covers <see cref="Region"/>.</param>
/// <param name="ZoneId">The zone in the trigger's zone-set that hit the overlap.</param>
internal sealed record OverlapHit(BodyRegion Region, string OtherBackendId, string ZoneId);

/// <summary>
/// Daemon-wide singleton holding the result of the bodymap validation
/// pass. <c>BackendBootstrapper</c> populates it once after every
/// backend has registered (or been declined); <c>TriggerCoordinator</c>
/// reads it on every trigger to enforce <see cref="OverlapPolicy.Refuse"/>.
/// </summary>
internal interface IBodyMapState
{
    /// <summary>
    /// Configured overlap policy. Coordinator skips its overlap check
    /// when this is anything other than <see cref="OverlapPolicy.Refuse"/>.
    /// </summary>
    OverlapPolicy OverlapPolicy { get; }

    /// <summary>
    /// Total declared placements (after the validator collapsed
    /// invalid entries). Surfaced on the startup banner.
    /// </summary>
    int PlacementCount { get; }

    /// <summary>
    /// Number of overlap warnings the validator emitted. Surfaced on
    /// the startup banner.
    /// </summary>
    int WarningCount { get; }

    /// <summary>
    /// Returns the first overlap hit between <paramref name="backend"/>'s
    /// trigger zone-set and another registered backend's coverage,
    /// or <c>null</c> when there is no overlap. Caller is expected to
    /// gate the call on <see cref="OverlapPolicy"/>; this method does
    /// not check the policy itself so unit tests can exercise it
    /// without configuring policy each time.
    /// </summary>
    OverlapHit? CheckOverlap(IHapticBackend backend, IReadOnlyList<string> zoneIds);
}

internal sealed class BodyMapState : IBodyMapState
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<BodyRegion>>
        EmptyRegionsByBackend =
            ImmutableDictionary<string, IReadOnlySet<BodyRegion>>.Empty;

    private static readonly IReadOnlyDictionary<BodyRegion, IReadOnlySet<string>>
        EmptyBackendsByRegion =
            ImmutableDictionary<BodyRegion, IReadOnlySet<string>>.Empty;

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, BodyRegion>>
        EmptyZoneRegions =
            ImmutableDictionary<string, IReadOnlyDictionary<string, BodyRegion>>.Empty;

    public OverlapPolicy OverlapPolicy { get; private set; } = OverlapPolicy.Warn;

    public int PlacementCount { get; private set; }

    public int WarningCount { get; private set; }

    public IReadOnlyDictionary<string, IReadOnlySet<BodyRegion>> RegionsByBackend { get; private set; }
        = EmptyRegionsByBackend;

    public IReadOnlyDictionary<BodyRegion, IReadOnlySet<string>> BackendsByRegion { get; private set; }
        = EmptyBackendsByRegion;

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, BodyRegion>> ZoneRegions { get; private set; }
        = EmptyZoneRegions;

    /// <summary>
    /// Called by <c>BackendBootstrapper</c> exactly once during
    /// startup, after the validator runs. Forbidden-region errors are
    /// now fatal-throw rather than deregister-and-continue, so by the
    /// time this runs the body map is either valid (or only contains
    /// non-fatal <see cref="BodyMapErrorKind.BackendDeclined"/>
    /// warnings).
    /// </summary>
    public void Initialize(
        BodyMapValidationResult result,
        OverlapPolicy policy,
        int placementCount)
    {
        ArgumentNullException.ThrowIfNull(result);
        OverlapPolicy = policy;
        PlacementCount = placementCount;
        WarningCount = result.Warnings.Count;
        RegionsByBackend = result.RegionsByBackend;
        BackendsByRegion = result.BackendsByRegion;
        ZoneRegions = result.ZoneRegions;
    }

    /// <inheritdoc />
    public OverlapHit? CheckOverlap(IHapticBackend backend, IReadOnlyList<string> zoneIds)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(zoneIds);

        if (!ZoneRegions.TryGetValue(backend.Id, out var zoneMap))
        {
            // Backend has no declared placements — Unspecified-mode;
            // overlap is meaningless.
            return null;
        }

        // Expand groups so the placement zone-region map (which is
        // keyed on leaf zones the validator resolved) sees the same
        // ids the trigger ultimately addresses.
        var groupMembers = backend.Zones.Groups.ToDictionary(
            g => g.Id,
            g => (IReadOnlyList<string>)g.ZoneIds.ToArray(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var zoneId in zoneIds)
        {
            IEnumerable<string> leaves = groupMembers.TryGetValue(zoneId, out var members)
                ? members
                : new[] { zoneId };

            foreach (var leaf in leaves)
            {
                if (!zoneMap.TryGetValue(leaf, out var region))
                {
                    continue;
                }

                // Walk the inverse index and return the first other
                // backend whose declared region overlaps the trigger
                // zone's region. RegionHierarchy.Overlaps is symmetric
                // so a trigger on ChestFront finds another backend in
                // ChestOverHeart and vice-versa — without the index
                // having to pre-expand parent/child closures.
                foreach (var (otherRegion, backendsHere) in BackendsByRegion)
                {
                    if (!RegionHierarchy.Overlaps(region, otherRegion))
                    {
                        continue;
                    }

                    foreach (var otherId in backendsHere)
                    {
                        if (!string.Equals(otherId, backend.Id, StringComparison.OrdinalIgnoreCase))
                        {
                            return new OverlapHit(otherRegion, otherId, leaf);
                        }
                    }
                }
            }
        }

        return null;
    }
}

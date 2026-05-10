using System.Collections.Immutable;
using Smited.Daemon.Backends;
using Smited.Daemon.Configuration;

namespace Smited.Daemon.BodyMap;

/// <summary>
/// Validates the user-declared <see cref="BodyMapOptions.Placements"/>
/// against the backends actually registered at startup. Builds the
/// per-backend and per-region indices the trigger-time overlap check
/// consumes.
/// </summary>
internal sealed class BodyMapValidator
{
    /// <summary>
    /// Equality on the <c>(BackendId, ZoneId)</c> tuple under
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> on both members.
    /// Used by duplicate-zone detection so <c>pectoral_l</c> and
    /// <c>PECTORAL_L</c> resolve to the same key — without this the
    /// default tuple comparer would let a case-mismatched duplicate
    /// slip past <c>GroupBy</c> and then silently collide in the
    /// case-insensitive ZoneRegions index downstream.
    /// </summary>
    private static readonly IEqualityComparer<(string BackendId, string ZoneId)>
        BackendZoneKeyComparer = new BackendZoneKeyEqualityComparer();

    private sealed class BackendZoneKeyEqualityComparer
        : IEqualityComparer<(string BackendId, string ZoneId)>
    {
        public bool Equals(
            (string BackendId, string ZoneId) x,
            (string BackendId, string ZoneId) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.BackendId, y.BackendId)
            && StringComparer.OrdinalIgnoreCase.Equals(x.ZoneId, y.ZoneId);

        public int GetHashCode((string BackendId, string ZoneId) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.BackendId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ZoneId));
    }

    /// <summary>
    /// Convenience overload that treats every registered backend as
    /// also-declared. Useful for unit tests that don't exercise the
    /// declined-vs-typo distinction; production code uses the three-arg
    /// overload so a placement targeting a declared-but-declined backend
    /// surfaces as <see cref="BodyMapErrorKind.BackendDeclined"/> rather
    /// than as a fatal <see cref="BodyMapErrorKind.UnknownBackend"/>.
    /// </summary>
    public BodyMapValidationResult Validate(
        IReadOnlyCollection<IHapticBackend> backends,
        BodyMapOptions options)
    {
        ArgumentNullException.ThrowIfNull(backends);
        return Validate(backends, backends.Select(b => b.Id).ToArray(), options);
    }

    /// <summary>
    /// Validate placements against registered backends. The bootstrapper
    /// consumes the result: every error kind except
    /// <see cref="BodyMapErrorKind.BackendDeclined"/> is fatal-throw;
    /// <see cref="BodyMapErrorKind.BackendDeclined"/> logs at WARN and
    /// startup continues. Warnings (overlap detection) log at WARN.
    /// </summary>
    /// <param name="backends">
    /// Backends actually registered in <c>BackendRegistry</c> after
    /// every factory has run. Subset of <paramref name="allDeclaredBackendIds"/>:
    /// declared backends whose factory returned <c>null</c> are absent
    /// from this collection.
    /// </param>
    /// <param name="allDeclaredBackendIds">
    /// Every backend id the user declared in
    /// <c>Smited:Backends:Items</c> (post-synthesis of the
    /// empty-Items default). Lets the validator distinguish a placement
    /// for a declared-but-declined backend (warn) from a placement
    /// whose backend id is a typo (fatal). The daemon would otherwise
    /// refuse to start on every Mac whose user has an
    /// <c>owo_skin</c> descriptor.
    /// </param>
    /// <param name="options">User-supplied bodymap configuration.</param>
    public BodyMapValidationResult Validate(
        IReadOnlyCollection<IHapticBackend> backends,
        IReadOnlyCollection<string> allDeclaredBackendIds,
        BodyMapOptions options)
    {
        ArgumentNullException.ThrowIfNull(backends);
        ArgumentNullException.ThrowIfNull(allDeclaredBackendIds);
        ArgumentNullException.ThrowIfNull(options);

        var declaredBackendIds = allDeclaredBackendIds
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var errors = new List<BodyMapError>();
        var warnings = new List<BodyMapWarning>();

        // backendId → backend, for quick lookup. Case-insensitive
        // matches BackendRegistry's keying.
        var backendById = backends.ToDictionary(
            b => b.Id, StringComparer.OrdinalIgnoreCase);

        // regionsByBackend[backendId] = direct placement-declared regions
        // for that backend. NOT expanded with parent/child closures —
        // RegionHierarchy.Overlaps handles intersection at query time,
        // so the index stays a faithful record of what the user actually
        // declared.
        var regionsByBackend = new Dictionary<string, HashSet<BodyRegion>>(
            StringComparer.OrdinalIgnoreCase);

        // zoneRegions[backendId][leafZoneId] = declared region. Used
        // by the trigger-time overlap check to translate a trigger's
        // resolved zone set into regions touched. First-write-wins on
        // duplicates so a misconfiguration (one zone in two regions)
        // doesn't crash the validator; it surfaces as either a
        // forbidden-region error or just incoherent overlap output.
        var zoneRegions = new Dictionary<string, Dictionary<string, BodyRegion>>(
            StringComparer.OrdinalIgnoreCase);

        var smitedDefaults = SmitedDefaultForbiddenRegions.Default
            .Except(options.AllowOverrideRegions)
            .ToImmutableHashSet();

        // Pass 0: surface placements with no zones. The expand pass
        // would otherwise no-op for them, leaving the misconfiguration
        // invisible (no errors, no index entries) while still inflating
        // BodyMapState.PlacementCount on the banner. Empty/null
        // ZoneIds is a fatal validation error.
        foreach (var placement in options.Placements)
        {
            if (placement.ZoneIds is null || placement.ZoneIds.Count == 0)
            {
                errors.Add(new BodyMapError(
                    placement.BackendId,
                    ZoneId: string.Empty,
                    placement.Region,
                    BodyMapErrorKind.EmptyPlacement,
                    $"Placement for backend '{placement.BackendId}' in region "
                    + $"'{placement.Region}' has no ZoneIds. A placement must "
                    + "declare at least one leaf zone or zone group."));
            }
        }

        // Pass 1: validate backend ids and expand each non-empty
        // placement into its (backend, leafZone, region, source)
        // tuples. UnknownBackend, BackendDeclined, and UnknownZone
        // errors land here. Placements with an Unspecified region
        // are silently skipped.
        var expanded = new List<ExpandedPlacement>();
        foreach (var placement in options.Placements)
        {
            if (placement.ZoneIds is null || placement.ZoneIds.Count == 0)
            {
                // Already reported in pass 0; downstream passes don't
                // need to defensively check.
                continue;
            }

            if (!backendById.TryGetValue(placement.BackendId, out var backend))
            {
                if (declaredBackendIds.Contains(placement.BackendId))
                {
                    errors.Add(new BodyMapError(
                        placement.BackendId,
                        ZoneId: "",
                        placement.Region,
                        BodyMapErrorKind.BackendDeclined,
                        $"Placement for '{placement.BackendId}' skipped: backend "
                        + "declared in Smited:Backends:Items but its factory declined "
                        + "to register it (typically wrong host OS or missing SDK "
                        + "runtime files)."));
                }
                else
                {
                    var hint = declaredBackendIds.Count > 0
                        ? $" Did you mean one of: {string.Join(", ", declaredBackendIds.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))}?"
                        : " No backends are declared in Smited:Backends:Items.";
                    errors.Add(new BodyMapError(
                        placement.BackendId,
                        ZoneId: "",
                        placement.Region,
                        BodyMapErrorKind.UnknownBackend,
                        $"Placement references backend '{placement.BackendId}' which is "
                        + "not declared in Smited:Backends:Items." + hint));
                }
                continue;
            }

            if (placement.Region == BodyRegion.Unspecified)
            {
                continue;
            }

            var knownLeafZones = backend.Zones.Zones
                .Select(z => z.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var groupMembers = backend.Zones.Groups.ToDictionary(
                g => g.Id,
                g => (IReadOnlyList<string>)g.ZoneIds.ToArray(),
                StringComparer.OrdinalIgnoreCase);

            foreach (var zoneId in placement.ZoneIds)
            {
                if (groupMembers.TryGetValue(zoneId, out var members))
                {
                    foreach (var member in members)
                    {
                        expanded.Add(new ExpandedPlacement(backend.Id, member, placement.Region));
                    }
                }
                else if (knownLeafZones.Contains(zoneId))
                {
                    expanded.Add(new ExpandedPlacement(backend.Id, zoneId, placement.Region));
                }
                else
                {
                    errors.Add(new BodyMapError(
                        backend.Id,
                        zoneId,
                        placement.Region,
                        BodyMapErrorKind.UnknownZone,
                        $"Zone '{zoneId}' is neither a leaf zone nor a group on backend '{backend.Id}'."));
                }
            }
        }

        // Pass 2: detect duplicates. A (BackendId, ZoneId) pair appearing
        // in more than one expanded entry means the user declared the
        // same leaf zone in multiple regions — either directly, or
        // implicitly via group + leaf-zone overlap. GroupBy expresses
        // "any pair appearing more than once" declaratively; the
        // previous accumulator-based first-write-wins approach hid this
        // intent in control flow and silently dropped the second region.
        var duplicateGroups = expanded
            .GroupBy(x => (x.BackendId, x.ZoneId), BackendZoneKeyComparer)
            .Where(g => g.Count() > 1)
            .ToArray();

        foreach (var group in duplicateGroups)
        {
            var regions = group.Select(x => x.Region).Distinct().ToArray();
            errors.Add(new BodyMapError(
                BackendId: group.Key.BackendId,
                ZoneId: group.Key.ZoneId,
                Region: regions[0],
                Kind: BodyMapErrorKind.DuplicateZonePlacement,
                Message: $"Zone '{group.Key.ZoneId}' on backend '{group.Key.BackendId}' "
                    + $"declared in multiple regions: {string.Join(", ", regions)}. "
                    + "A zone may only occupy one region. Consolidate the placements "
                    + "(note: a placement using a zone group expands to every member "
                    + "zone, so a group + leaf-zone combination can implicitly "
                    + "duplicate a leaf)."));
        }

        var duplicateKeys = duplicateGroups
            .Select(g => g.Key)
            .ToHashSet(BackendZoneKeyComparer);

        // Pass 3: for non-duplicate entries, run forbidden-region checks
        // and build the regionsByBackend / zoneRegions indices. Walk
        // the FORBIDDEN regions per leaf and ask whether the placement
        // region overlaps each — RegionHierarchy.Overlaps is symmetric
        // so a placement on a parent (ChestFront) trips a forbidden
        // child (ChestOverHeart) and vice-versa.
        foreach (var entry in expanded.Where(
            x => !duplicateKeys.Contains((x.BackendId, x.ZoneId))))
        {
            var backend = backendById[entry.BackendId];

            BodyRegion? citedManufacturer = null;
            foreach (var forbidden in backend.ForbiddenRegions)
            {
                if (RegionHierarchy.Overlaps(entry.Region, forbidden))
                {
                    citedManufacturer = forbidden;
                    break;
                }
            }

            if (citedManufacturer is BodyRegion mfBan)
            {
                errors.Add(new BodyMapError(
                    entry.BackendId,
                    entry.ZoneId,
                    mfBan,
                    BodyMapErrorKind.ManufacturerForbidden,
                    $"Placement of zone '{entry.ZoneId}' on backend '{entry.BackendId}' "
                    + $"in region '{entry.Region}' overlaps the backend's "
                    + $"manufacturer forbidden region '{mfBan}'."));
            }
            else
            {
                BodyRegion? citedDefault = null;
                foreach (var forbidden in smitedDefaults)
                {
                    if (RegionHierarchy.Overlaps(entry.Region, forbidden))
                    {
                        citedDefault = forbidden;
                        break;
                    }
                }

                if (citedDefault is BodyRegion defaultBan)
                {
                    errors.Add(new BodyMapError(
                        entry.BackendId,
                        entry.ZoneId,
                        defaultBan,
                        BodyMapErrorKind.SmitedDefaultForbidden,
                        $"Placement of zone '{entry.ZoneId}' on backend '{entry.BackendId}' "
                        + $"in region '{entry.Region}' overlaps smited's default "
                        + $"forbidden region '{defaultBan}'. Add '{defaultBan}' to "
                        + "Smited:BodyMap:AllowOverrideRegions to opt out."));
                }
            }

            // Even when forbidden-region errors fire for this entry,
            // contribute to the regions / zones indices so overlap
            // analysis sees the intended coverage. The bootstrapper
            // throws on forbidden errors so the indices are only
            // consulted when there are no fatal errors — but the
            // populated state is also useful in tests that assert
            // on the result's index shape.
            if (!regionsByBackend.TryGetValue(entry.BackendId, out var set))
            {
                set = new HashSet<BodyRegion>();
                regionsByBackend[entry.BackendId] = set;
            }
            set.Add(entry.Region);

            if (!zoneRegions.TryGetValue(entry.BackendId, out var zoneMap))
            {
                zoneMap = new Dictionary<string, BodyRegion>(StringComparer.OrdinalIgnoreCase);
                zoneRegions[entry.BackendId] = zoneMap;
            }
            zoneMap[entry.ZoneId] = entry.Region;
        }

        // Build the inverse index: region → set of backend ids that
        // declared placements on that exact region. The trigger-time
        // overlap check still walks via RegionHierarchy.Overlaps, so
        // anatomical relationships (ChestFront ↔ ChestOverHeart) are
        // resolved at query time.
        var backendsByRegion = new Dictionary<BodyRegion, HashSet<string>>();
        foreach (var (backendId, regions) in regionsByBackend)
        {
            foreach (var region in regions)
            {
                if (!backendsByRegion.TryGetValue(region, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    backendsByRegion[region] = set;
                }
                set.Add(backendId);
            }
        }

        if (options.OverlapPolicy != OverlapPolicy.Off)
        {
            warnings.AddRange(BuildOverlapWarnings(regionsByBackend, options.Placements));
        }

        return new BodyMapValidationResult(
            errors,
            warnings,
            regionsByBackend.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlySet<BodyRegion>)kv.Value.ToImmutableHashSet(),
                StringComparer.OrdinalIgnoreCase),
            backendsByRegion.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlySet<string>)kv.Value.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase)),
            zoneRegions.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyDictionary<string, BodyRegion>)kv.Value
                    .ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase));
    }

    private static IEnumerable<BodyMapWarning> BuildOverlapWarnings(
        IReadOnlyDictionary<string, HashSet<BodyRegion>> regionsByBackend,
        IReadOnlyList<Placement> placements)
    {
        // Pairwise scan over distinct (backend, region) declarations.
        // Two placements warn if their regions overlap anatomically —
        // same region, parent-of, or child-of. Emit one warning per
        // overlapping region pair, deduped so ChestFront ↔
        // ChestOverHeart yields a single entry rather than one per
        // direction.
        var declarations = regionsByBackend
            .SelectMany(kv => kv.Value.Select(r => (BackendId: kv.Key, Region: r)))
            .ToList();

        var seen = new HashSet<(BodyRegion A, BodyRegion B, string IdA, string IdB)>();
        var seenPairKeys = new HashSet<string>();

        for (var i = 0; i < declarations.Count; i++)
        {
            for (var j = i + 1; j < declarations.Count; j++)
            {
                var (idA, regA) = declarations[i];
                var (idB, regB) = declarations[j];
                if (string.Equals(idA, idB, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!RegionHierarchy.Overlaps(regA, regB))
                {
                    continue;
                }

                // De-dup by sorted (id, region) tuple so two backends
                // that each placed two zones in the same region don't
                // warn twice.
                var sortedIds = string.CompareOrdinal(idA, idB) <= 0
                    ? (idA, idB) : (idB, idA);
                var sortedRegions = (int)regA <= (int)regB ? (regA, regB) : (regB, regA);
                var key = $"{sortedIds.Item1}|{sortedIds.Item2}|{sortedRegions.Item1}|{sortedRegions.Item2}";
                if (!seenPairKeys.Add(key))
                {
                    continue;
                }

                var zonesA = ZoneIdsFor(placements, idA, regA);
                var zonesB = ZoneIdsFor(placements, idB, regB);

                yield return new BodyMapWarning(
                    regA,
                    new[] { (idA, zonesA), (idB, zonesB) },
                    regA == regB
                        ? $"Region '{regA}' is covered by both '{idA}' and '{idB}'."
                        : $"Regions '{regA}' (on '{idA}') and '{regB}' (on '{idB}') "
                          + "overlap anatomically.");
            }
        }
    }

    private static IReadOnlyList<string> ZoneIdsFor(
        IReadOnlyList<Placement> placements,
        string backendId,
        BodyRegion region) =>
        placements
            .Where(p =>
                string.Equals(p.BackendId, backendId, StringComparison.OrdinalIgnoreCase)
                && p.Region == region)
            .SelectMany(p => p.ZoneIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// One backend / leaf-zone / region tuple after the per-placement
    /// expansion pass. Group references in <c>Placement.ZoneIds</c>
    /// produce one entry per member leaf zone here, so duplicate-detection
    /// catches both direct duplicates and implicit duplicates via group
    /// + leaf-zone overlap.
    /// </summary>
    private sealed record ExpandedPlacement(
        string BackendId,
        string ZoneId,
        BodyRegion Region);
}

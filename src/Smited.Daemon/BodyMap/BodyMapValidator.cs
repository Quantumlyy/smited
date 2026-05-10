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
    /// Validate placements against registered backends. The
    /// bootstrapper consumes the result: forbidden-region errors
    /// drive backend deregistration, unknown-backend / unknown-zone
    /// errors abort startup, warnings log at WARN level.
    /// </summary>
    public BodyMapValidationResult Validate(
        IReadOnlyCollection<IHapticBackend> backends,
        BodyMapOptions options)
    {
        ArgumentNullException.ThrowIfNull(backends);
        ArgumentNullException.ThrowIfNull(options);

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

        foreach (var placement in options.Placements)
        {
            if (!backendById.TryGetValue(placement.BackendId, out var backend))
            {
                errors.Add(new BodyMapError(
                    placement.BackendId,
                    ZoneId: "",
                    placement.Region,
                    BodyMapErrorKind.UnknownBackend,
                    $"Placement references backend '{placement.BackendId}' which is not registered."));
                continue;
            }

            if (placement.Region == BodyRegion.Unspecified)
            {
                // Allowed but useless: the placement contributes nothing
                // to forbidden-region or overlap checks. No error.
                continue;
            }

            var knownLeafZones = backend.Zones.Zones
                .Select(z => z.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var groupMembers = backend.Zones.Groups.ToDictionary(
                g => g.Id,
                g => (IReadOnlyList<string>)g.ZoneIds.ToArray(),
                StringComparer.OrdinalIgnoreCase);

            // Resolve every zone (leaf or group) into its leaf set;
            // unknown ids surface as UnknownZone errors. Don't dedup
            // across placements — each placement's zones contribute
            // independently to the regionsByBackend index.
            var resolvedLeafZones = new List<string>();
            foreach (var zoneId in placement.ZoneIds)
            {
                if (groupMembers.TryGetValue(zoneId, out var members))
                {
                    resolvedLeafZones.AddRange(members);
                }
                else if (knownLeafZones.Contains(zoneId))
                {
                    resolvedLeafZones.Add(zoneId);
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

            // For each leaf zone, run the forbidden-region checks. Walk
            // the FORBIDDEN regions and ask whether the placement's
            // region overlaps each — symmetric so a placement on a
            // parent (ChestFront) trips a forbidden child (ChestOverHeart)
            // and vice-versa. Manufacturer first (non-overridable),
            // smited-default second.
            foreach (var zone in resolvedLeafZones)
            {
                BodyRegion? citedManufacturer = null;
                foreach (var forbidden in backend.ForbiddenRegions)
                {
                    if (RegionHierarchy.Overlaps(placement.Region, forbidden))
                    {
                        citedManufacturer = forbidden;
                        break;
                    }
                }

                if (citedManufacturer is BodyRegion mfBan)
                {
                    errors.Add(new BodyMapError(
                        backend.Id,
                        zone,
                        mfBan,
                        BodyMapErrorKind.ManufacturerForbidden,
                        $"Placement of zone '{zone}' on backend '{backend.Id}' "
                        + $"in region '{placement.Region}' overlaps the backend's "
                        + $"manufacturer forbidden region '{mfBan}'."));
                    continue;
                }

                BodyRegion? citedDefault = null;
                foreach (var forbidden in smitedDefaults)
                {
                    if (RegionHierarchy.Overlaps(placement.Region, forbidden))
                    {
                        citedDefault = forbidden;
                        break;
                    }
                }

                if (citedDefault is BodyRegion defaultBan)
                {
                    errors.Add(new BodyMapError(
                        backend.Id,
                        zone,
                        defaultBan,
                        BodyMapErrorKind.SmitedDefaultForbidden,
                        $"Placement of zone '{zone}' on backend '{backend.Id}' "
                        + $"in region '{placement.Region}' overlaps smited's default "
                        + $"forbidden region '{defaultBan}'. Add '{defaultBan}' to "
                        + "Smited:BodyMap:AllowOverrideRegions to opt out."));
                }
            }

            // Even if errors fire, contribute to the regions index so
            // overlap analysis sees the intended coverage. Forbidden
            // backends will get deregistered before the index is
            // consulted by the trigger-time check.
            if (!regionsByBackend.TryGetValue(backend.Id, out var set))
            {
                set = new HashSet<BodyRegion>();
                regionsByBackend[backend.Id] = set;
            }
            set.Add(placement.Region);

            if (!zoneRegions.TryGetValue(backend.Id, out var zoneMap))
            {
                zoneMap = new Dictionary<string, BodyRegion>(StringComparer.OrdinalIgnoreCase);
                zoneRegions[backend.Id] = zoneMap;
            }
            foreach (var leaf in resolvedLeafZones)
            {
                zoneMap.TryAdd(leaf, placement.Region);
            }
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
}

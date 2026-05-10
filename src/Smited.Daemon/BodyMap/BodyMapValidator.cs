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

        // regionsByBackend[backendId] = set of regions that backend covers.
        // Built by walking every placement: each placement contributes
        // its declared region to the set, and the parent regions
        // (RegionHierarchy.ContainingRegions) follow because a forbidden
        // ChestFront should also cover ChestOverHeart.
        var regionsByBackend = new Dictionary<string, HashSet<BodyRegion>>(
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

            // For each leaf zone, run the forbidden-region checks
            // (manufacturer first, smited-default second), walking
            // up the region hierarchy so subregion declarations
            // inherit parent forbiddenness.
            var containing = RegionHierarchy.ContainingRegions(placement.Region);
            foreach (var zone in resolvedLeafZones)
            {
                foreach (var ancestor in containing)
                {
                    if (backend.ForbiddenRegions.Contains(ancestor))
                    {
                        errors.Add(new BodyMapError(
                            backend.Id,
                            zone,
                            placement.Region,
                            BodyMapErrorKind.ManufacturerForbidden,
                            $"Placement of zone '{zone}' on backend '{backend.Id}' "
                            + $"in region '{placement.Region}' violates the backend's "
                            + $"manufacturer forbidden region '{ancestor}'."));
                        // First match wins; no point flagging
                        // ancestor + descendant of the same chain.
                        break;
                    }
                    if (smitedDefaults.Contains(ancestor))
                    {
                        errors.Add(new BodyMapError(
                            backend.Id,
                            zone,
                            placement.Region,
                            BodyMapErrorKind.SmitedDefaultForbidden,
                            $"Placement of zone '{zone}' on backend '{backend.Id}' "
                            + $"in region '{placement.Region}' lands in smited's default "
                            + $"forbidden region '{ancestor}'. Add '{ancestor}' to "
                            + "Smited:BodyMap:AllowOverrideRegions to opt out."));
                        break;
                    }
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
            foreach (var ancestor in containing)
            {
                set.Add(ancestor);
            }
        }

        // Build the inverse index: region → set of backend ids that
        // cover the region. Used both for warnings here and for
        // trigger-time overlap rejection in BodyMapState.
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
            foreach (var (region, backendIds) in backendsByRegion)
            {
                if (backendIds.Count <= 1)
                {
                    continue;
                }

                var overlapZones = backendIds
                    .Select(id => (id, (IReadOnlyList<string>)
                        options.Placements
                            .Where(p => string.Equals(p.BackendId, id, StringComparison.OrdinalIgnoreCase)
                                && RegionHierarchy.ContainingRegions(p.Region).Contains(region))
                            .SelectMany(p => p.ZoneIds)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray()))
                    .ToList();

                warnings.Add(new BodyMapWarning(
                    region,
                    overlapZones,
                    $"Region '{region}' is covered by {backendIds.Count} backends: "
                    + string.Join(", ", backendIds.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))));
            }
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
                kv => (IReadOnlySet<string>)kv.Value.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase)));
    }
}

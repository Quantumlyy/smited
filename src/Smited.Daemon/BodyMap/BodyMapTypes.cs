namespace Smited.Daemon.BodyMap;

/// <summary>
/// Outcome of <see cref="BodyMapValidator"/>: every error
/// (severity-graded by <see cref="BodyMapErrorKind"/>), every warning,
/// and the forward / inverse / per-zone indices the trigger-time
/// overlap check needs.
/// </summary>
/// <param name="Errors">Per-placement errors. See <see cref="BodyMapErrorKind"/>.</param>
/// <param name="Warnings">Multi-backend overlap warnings.</param>
/// <param name="RegionsByBackend">
/// Resolved index: backend id → set of regions the backend covers
/// (after group expansion and parent-region inheritance).
/// </param>
/// <param name="BackendsByRegion">
/// Inverse index: region → set of backend ids that cover the region.
/// Used by trigger-time overlap rejection.
/// </param>
/// <param name="ZoneRegions">
/// Per-(backend, leaf zone) → declared region. Lets the trigger
/// coordinator translate a trigger's resolved zone set into the
/// regions it touches without re-running the validator.
/// </param>
internal sealed record BodyMapValidationResult(
    IReadOnlyList<BodyMapError> Errors,
    IReadOnlyList<BodyMapWarning> Warnings,
    IReadOnlyDictionary<string, IReadOnlySet<BodyRegion>> RegionsByBackend,
    IReadOnlyDictionary<BodyRegion, IReadOnlySet<string>> BackendsByRegion,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, BodyRegion>> ZoneRegions);

/// <summary>
/// One placement-level error. <see cref="Kind"/> drives whether the
/// bootstrapper aborts startup. All kinds except
/// <see cref="BodyMapErrorKind.BackendDeclined"/> are fatal — the
/// daemon refuses to start until the user fixes the configuration.
/// <see cref="BodyMapErrorKind.BackendDeclined"/> is environmental
/// (e.g. an OWO descriptor on a Mac host) and surfaces as a warning;
/// the daemon starts with the declared-but-declined backend simply
/// absent.
/// </summary>
internal sealed record BodyMapError(
    string BackendId,
    string ZoneId,
    BodyRegion Region,
    BodyMapErrorKind Kind,
    string Message);

internal enum BodyMapErrorKind
{
    /// <summary>
    /// Placement lands in the backend's manufacturer-mandated forbidden
    /// list. Non-overridable; fatal — the user must remove or relocate
    /// the placement.
    /// </summary>
    ManufacturerForbidden,

    /// <summary>
    /// Placement lands in <c>SmitedDefaultForbiddenRegions</c> and the
    /// user has not added the region to <c>AllowOverrideRegions</c>.
    /// Fatal until the user opts out of the default or relocates the
    /// placement.
    /// </summary>
    SmitedDefaultForbidden,

    /// <summary>
    /// Placement references a backend id that is not declared in any
    /// <see cref="Configuration.BackendDescriptor"/> entry. Configuration
    /// mistake (typically a typo); fatal — the user must fix the id and
    /// restart. The error message includes a "did you mean" list of the
    /// declared ids.
    /// </summary>
    UnknownBackend,

    /// <summary>
    /// Placement references a backend id that IS declared but whose
    /// factory declined to create the backend (wrong host OS, missing
    /// SDK assembly, etc.). Environmental, not a configuration error.
    /// Surfaced as a warning; the placement is skipped and daemon
    /// startup continues. Distinct from
    /// <see cref="UnknownBackend"/> so a user running an OWO-targeting
    /// configuration on Mac doesn't have their daemon refuse to start.
    /// </summary>
    BackendDeclined,

    /// <summary>
    /// Placement.ZoneIds contains an id that is neither a leaf zone
    /// nor a group on the named backend. User-fixable typo; fatal.
    /// </summary>
    UnknownZone,

    /// <summary>
    /// The same (backend, leaf zone) pair appears in more than one
    /// placement. A zone occupies one region; multi-region zones are
    /// not modelled today. Fatal — the user must consolidate or split
    /// the placements.
    /// </summary>
    /// <remarks>
    /// Duplication can arise implicitly via group expansion: a
    /// placement declaring a zone group (e.g. <c>arms</c>) expands
    /// to every member zone, so a group + leaf-zone combination
    /// touching the same leaf is also a duplicate. The validator
    /// catches both shapes after group expansion.
    /// </remarks>
    DuplicateZonePlacement,
}

/// <summary>
/// Multi-backend overlap warning emitted when two or more registered
/// backends cover the same body region. Logged at WARN level
/// regardless of <c>OverlapPolicy</c> (except <c>Off</c>); under
/// <c>Refuse</c>, the trigger-time check additionally rejects
/// individual triggers that cross overlapping regions.
/// </summary>
internal sealed record BodyMapWarning(
    BodyRegion Region,
    IReadOnlyList<(string BackendId, IReadOnlyList<string> ZoneIds)> Overlaps,
    string Message);

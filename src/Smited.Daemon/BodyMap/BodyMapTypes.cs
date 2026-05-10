namespace Smited.Daemon.BodyMap;

/// <summary>
/// Outcome of <see cref="BodyMapValidator.Validate"/>: every error
/// (severity-graded by <see cref="BodyMapErrorKind"/>), every warning,
/// and the forward and inverse indices the trigger-time overlap check
/// needs.
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
internal sealed record BodyMapValidationResult(
    IReadOnlyList<BodyMapError> Errors,
    IReadOnlyList<BodyMapWarning> Warnings,
    IReadOnlyDictionary<string, IReadOnlySet<BodyRegion>> RegionsByBackend,
    IReadOnlyDictionary<BodyRegion, IReadOnlySet<string>> BackendsByRegion);

/// <summary>
/// One placement-level error. <see cref="Kind"/> drives whether the
/// bootstrapper deregisters the offending backend
/// (<see cref="BodyMapErrorKind.ManufacturerForbidden"/> /
/// <see cref="BodyMapErrorKind.SmitedDefaultForbidden"/>) or aborts
/// startup outright (<see cref="BodyMapErrorKind.UnknownBackend"/> /
/// <see cref="BodyMapErrorKind.UnknownZone"/>).
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
    /// list. Non-overridable; the backend gets deregistered.
    /// </summary>
    ManufacturerForbidden,

    /// <summary>
    /// Placement lands in <c>SmitedDefaultForbiddenRegions</c> and the
    /// user has not added the region to <c>AllowOverrideRegions</c>.
    /// Overridable.
    /// </summary>
    SmitedDefaultForbidden,

    /// <summary>
    /// Placement.BackendId does not match any registered backend.
    /// User-fixable typo; aborts startup.
    /// </summary>
    UnknownBackend,

    /// <summary>
    /// Placement.ZoneIds contains an id that is neither a leaf zone
    /// nor a group on the named backend. User-fixable typo; aborts
    /// startup.
    /// </summary>
    UnknownZone,
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

using System.Collections.Immutable;

namespace Smited.Daemon.BodyMap;

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
    /// Number of backends the bootstrapper deregistered because of
    /// forbidden-region errors. Surfaced on the startup banner.
    /// </summary>
    int RefusedBackendCount { get; }

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
}

internal sealed class BodyMapState : IBodyMapState
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<BodyRegion>>
        EmptyRegionsByBackend =
            ImmutableDictionary<string, IReadOnlySet<BodyRegion>>.Empty;

    private static readonly IReadOnlyDictionary<BodyRegion, IReadOnlySet<string>>
        EmptyBackendsByRegion =
            ImmutableDictionary<BodyRegion, IReadOnlySet<string>>.Empty;

    public OverlapPolicy OverlapPolicy { get; private set; } = OverlapPolicy.Warn;

    public int RefusedBackendCount { get; private set; }

    public int PlacementCount { get; private set; }

    public int WarningCount { get; private set; }

    public IReadOnlyDictionary<string, IReadOnlySet<BodyRegion>> RegionsByBackend { get; private set; }
        = EmptyRegionsByBackend;

    public IReadOnlyDictionary<BodyRegion, IReadOnlySet<string>> BackendsByRegion { get; private set; }
        = EmptyBackendsByRegion;

    /// <summary>
    /// Called by <c>BackendBootstrapper</c> exactly once during
    /// startup, after the validator runs and refused backends have
    /// been deregistered.
    /// </summary>
    public void Initialize(
        BodyMapValidationResult result,
        OverlapPolicy policy,
        int placementCount,
        int refusedBackendCount)
    {
        ArgumentNullException.ThrowIfNull(result);
        OverlapPolicy = policy;
        PlacementCount = placementCount;
        RefusedBackendCount = refusedBackendCount;
        WarningCount = result.Warnings.Count;
        RegionsByBackend = result.RegionsByBackend;
        BackendsByRegion = result.BackendsByRegion;
    }
}

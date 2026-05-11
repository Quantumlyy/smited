using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Smited.Daemon.BodyMap;

namespace Smited.Daemon.Backends.Mock;

/// <summary>
/// Cross-platform fake for the bHaptics Tactosy for Feet,
/// parameterized by <c>side</c>. Same zone topology, motor count,
/// and forbidden-region set as the real <c>BhapticsFeetBackend</c>.
/// </summary>
public sealed class MockBhapticsFeetBackend : MockBhapticsBackendBase
{
    private readonly bool _isLeft;

    public MockBhapticsFeetBackend(
        string side,
        TimeProvider time,
        ILogger<MockBhapticsFeetBackend> logger)
        : base(
            time,
            logger,
            defaultId: ResolveIsLeft(side) ? "mock-bhaptics-feet-l" : "mock-bhaptics-feet-r",
            defaultDisplayName: ResolveIsLeft(side)
                ? "Mock bHaptics Tactosy for Feet (left)"
                : "Mock bHaptics Tactosy for Feet (right)",
            zones: BhapticsZoneTopology.BuildFeet(ResolveIsLeft(side)),
            forbiddenRegions: ImmutableHashSet<BodyRegion>.Empty)
    {
        _isLeft = ResolveIsLeft(side);
    }

    // Advertises the REAL kind so sensations are portable real↔mock;
    // see MockBhapticsVestBackend.Kind comment.
    public override string Kind => _isLeft ? "bhaptics_feet_l" : "bhaptics_feet_r";
    public override string DeviceKey => _isLeft ? "feet_l" : "feet_r";
    public override int MotorCount => 3;

    protected override IReadOnlyList<int> MotorsForZone(string zoneId) =>
        BhapticsMotorMap.FeetMotorsForZone(zoneId, _isLeft);

    private static bool ResolveIsLeft(string side)
    {
        ArgumentException.ThrowIfNullOrEmpty(side);
        if (string.Equals(side, "left", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(side, "right", StringComparison.OrdinalIgnoreCase)) return false;
        throw new ArgumentException(
            $"MockBhapticsFeetBackend side must be 'left' or 'right', got '{side}'",
            nameof(side));
    }
}

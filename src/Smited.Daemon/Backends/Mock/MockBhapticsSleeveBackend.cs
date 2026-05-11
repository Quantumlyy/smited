using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Smited.Daemon.BodyMap;

namespace Smited.Daemon.Backends.Mock;

/// <summary>
/// Cross-platform fake for the bHaptics TactSleeve, parameterized by
/// <c>side</c>. Same zone topology, motor count, and forbidden-region
/// set as the real <c>BhapticsSleeveBackend</c>; Program.cs registers
/// two keyed singletons (one per side) so the factory can resolve the
/// correct instance per descriptor kind.
/// </summary>
public sealed class MockBhapticsSleeveBackend : MockBhapticsBackendBase
{
    private readonly bool _isLeft;

    public MockBhapticsSleeveBackend(
        string side,
        TimeProvider time,
        ILogger<MockBhapticsSleeveBackend> logger)
        : base(
            time,
            logger,
            defaultId: ResolveIsLeft(side) ? "mock-bhaptics-sleeve-l" : "mock-bhaptics-sleeve-r",
            defaultDisplayName: ResolveIsLeft(side)
                ? "Mock bHaptics TactSleeve (left)"
                : "Mock bHaptics TactSleeve (right)",
            zones: BhapticsZoneTopology.BuildSleeve(ResolveIsLeft(side)),
            forbiddenRegions: ImmutableHashSet<BodyRegion>.Empty)
    {
        _isLeft = ResolveIsLeft(side);
    }

    public override string Kind => _isLeft ? "mock_bhaptics_sleeve_l" : "mock_bhaptics_sleeve_r";
    public override string DeviceKey => _isLeft ? "sleeve_l" : "sleeve_r";
    public override int MotorCount => 6;

    protected override IReadOnlyList<int> MotorsForZone(string zoneId) =>
        BhapticsMotorMap.SleeveMotorsForZone(zoneId, _isLeft);

    private static bool ResolveIsLeft(string side)
    {
        ArgumentException.ThrowIfNullOrEmpty(side);
        if (string.Equals(side, "left", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(side, "right", StringComparison.OrdinalIgnoreCase)) return false;
        throw new ArgumentException(
            $"MockBhapticsSleeveBackend side must be 'left' or 'right', got '{side}'",
            nameof(side));
    }
}

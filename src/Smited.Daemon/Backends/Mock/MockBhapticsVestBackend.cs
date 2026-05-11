using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Smited.Daemon.BodyMap;

namespace Smited.Daemon.Backends.Mock;

/// <summary>
/// Cross-platform fake for the bHaptics TactSuit X40. Mirrors
/// <see cref="MockOwoBackend"/>'s shape and uses the same zone
/// topology, motor count, parameter schema, and forbidden-region
/// set as the real <c>BhapticsVestBackend</c> on Windows.
/// </summary>
public sealed class MockBhapticsVestBackend : MockBhapticsBackendBase
{
    public MockBhapticsVestBackend(TimeProvider time, ILogger<MockBhapticsVestBackend> logger)
        : base(
            time,
            logger,
            defaultId: "mock-bhaptics-vest",
            defaultDisplayName: "Mock bHaptics TactSuit",
            zones: BhapticsZoneTopology.BuildVest(),
            forbiddenRegions: ImmutableHashSet.Create(
                BodyRegion.Head,
                BodyRegion.Face,
                BodyRegion.Throat,
                BodyRegion.Neck,
                BodyRegion.ChestOverHeart))
    {
    }

    // Advertises the REAL kind (bhaptics_vest) so a sensation file
    // declaring backend_kind=bhaptics_vest binds to either this mock
    // OR the real BhapticsVestBackend on Windows. Matches the
    // MockOwoBackend pattern: descriptor kind is "mock_bhaptics_vest"
    // (used by the factory to dispatch) but the advertised IHapticBackend.Kind
    // is the cross-backend hardware family.
    public override string Kind => "bhaptics_vest";
    public override string DeviceKey => "vest";
    public override int MotorCount => 40;

    protected override IReadOnlyList<int> MotorsForZone(string zoneId) =>
        BhapticsMotorMap.VestMotorsForZone(zoneId);
}

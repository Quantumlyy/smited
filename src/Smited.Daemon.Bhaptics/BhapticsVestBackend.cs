#if WINDOWS
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Smited.Daemon.Backends;
using Smited.Daemon.BodyMap;

namespace Smited.Daemon.Bhaptics;

/// <summary>
/// Real bHaptics TactSuit X40 backend. 40 actuators across the torso
/// (20 front, 20 back). Manufacturer-mandated forbidden regions cover
/// head/neck (no actuators there) plus the chest-over-heart
/// vibrotactile safety guidance.
/// </summary>
public sealed class BhapticsVestBackend : BhapticsBackendBase
{
    public BhapticsVestBackend(
        BhapticsVestOptions options,
        IBhapticsSdk sdk,
        TimeProvider time,
        ILogger<BhapticsVestBackend> logger)
        : base(
            options,
            sdk,
            time,
            logger,
            defaultDisplayName: "bHaptics TactSuit",
            zones: BhapticsZoneTopology.BuildVest(),
            forbiddenRegions: ImmutableHashSet.Create(
                BodyRegion.Head,
                BodyRegion.Face,
                BodyRegion.Throat,
                BodyRegion.Neck,
                BodyRegion.ChestOverHeart))
    {
    }

    public override string Kind => "bhaptics_vest";

    public override string DeviceKey => "vest";

    public override int MotorCount => 40;

    protected override IReadOnlyList<int> MotorsForZone(string zoneId) =>
        BhapticsMotorMap.VestMotorsForZone(zoneId);
}
#endif

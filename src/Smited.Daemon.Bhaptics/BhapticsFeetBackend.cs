#if WINDOWS
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Smited.Daemon.Backends;
using Smited.Daemon.BodyMap;

namespace Smited.Daemon.Bhaptics;

/// <summary>
/// Real bHaptics Tactosy for Feet backend, parameterized by side. 3
/// actuators per foot (heel, arch, toes). Two effective kinds —
/// <c>bhaptics_feet_l</c> and <c>bhaptics_feet_r</c> — share this
/// class; the factory passes <c>side</c> via constructor argument
/// based on the discriminator. No manufacturer-mandated forbidden
/// regions.
/// </summary>
public sealed class BhapticsFeetBackend : BhapticsBackendBase
{
    private readonly bool _isLeft;

    public BhapticsFeetBackend(
        string side,
        BhapticsFeetOptions options,
        IBhapticsSdk sdk,
        TimeProvider time,
        ILogger<BhapticsFeetBackend> logger)
        : base(
            options,
            sdk,
            time,
            logger,
            defaultDisplayName: ResolveDisplayName(side),
            zones: BhapticsZoneTopology.BuildFeet(ResolveIsLeft(side)),
            forbiddenRegions: ImmutableHashSet<BodyRegion>.Empty)
    {
        _isLeft = ResolveIsLeft(side);
    }

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
            $"BhapticsFeetBackend side must be 'left' or 'right', got '{side}'",
            nameof(side));
    }

    private static string ResolveDisplayName(string side) =>
        ResolveIsLeft(side) ? "bHaptics Tactosy for Feet (left)" : "bHaptics Tactosy for Feet (right)";
}
#endif

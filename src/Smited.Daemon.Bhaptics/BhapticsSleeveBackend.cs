#if WINDOWS
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Smited.Daemon.Backends;
using Smited.Daemon.BodyMap;

namespace Smited.Daemon.Bhaptics;

/// <summary>
/// Real bHaptics TactSleeve backend, parameterized by side. 6
/// actuators per arm running wrist→bicep. Two effective kinds —
/// <c>bhaptics_sleeve_l</c> and <c>bhaptics_sleeve_r</c> — share this
/// class; the factory passes <c>side</c> via constructor argument
/// based on the discriminator. No manufacturer-mandated forbidden
/// regions (arms have no vibrotactile safety bans).
/// </summary>
public sealed class BhapticsSleeveBackend : BhapticsBackendBase
{
    private readonly bool _isLeft;

    public BhapticsSleeveBackend(
        string side,
        BhapticsSleeveOptions options,
        IBhapticsSdk sdk,
        TimeProvider time,
        ILogger<BhapticsSleeveBackend> logger)
        : base(
            options,
            sdk,
            time,
            logger,
            defaultDisplayName: ResolveDisplayName(side),
            zones: BhapticsZoneTopology.BuildSleeve(ResolveIsLeft(side)),
            forbiddenRegions: ImmutableHashSet<BodyRegion>.Empty)
    {
        _isLeft = ResolveIsLeft(side);
    }

    public override string Kind => _isLeft ? "bhaptics_sleeve_l" : "bhaptics_sleeve_r";

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
            $"BhapticsSleeveBackend side must be 'left' or 'right', got '{side}'",
            nameof(side));
    }

    private static string ResolveDisplayName(string side) =>
        ResolveIsLeft(side) ? "bHaptics TactSleeve (left)" : "bHaptics TactSleeve (right)";
}
#endif

using FluentAssertions;
using Smited.Daemon.Backends;
using Xunit;

namespace Smited.Daemon.Tests.Backends;

/// <summary>
/// Pin the bHaptics zone topology output. Each backend's
/// <see cref="Smited.V1.ZoneTopology"/> is the contract gRPC clients
/// (admin UI, sensation authors) read to discover what zone IDs are
/// valid; cross-backend portability depends on the vest exposing the
/// same <c>pectoral_l</c> / <c>arm_l</c> / etc. IDs OWO does, and
/// device-specific IDs always carry the <c>bhaptics_</c> prefix.
/// </summary>
public sealed class BhapticsZoneTopologyTests
{
    [Fact]
    public void Vest_includes_cross_backend_portable_torso_zones()
    {
        var topology = BhapticsZoneTopology.BuildVest();
        var ids = topology.Zones.Select(z => z.Id).ToList();

        ids.Should().Contain(new[]
        {
            "pectoral_l", "pectoral_r",
            "abdominal_l", "abdominal_r",
            "lumbar_l", "lumbar_r",
            "dorsal_l", "dorsal_r",
        }, "vest must mirror OWO's torso zone IDs so sensations are portable");
    }

    [Fact]
    public void Vest_device_specific_zones_carry_bhaptics_prefix()
    {
        var topology = BhapticsZoneTopology.BuildVest();
        var portable = new HashSet<string>
        {
            "pectoral_l", "pectoral_r",
            "abdominal_l", "abdominal_r",
            "lumbar_l", "lumbar_r",
            "dorsal_l", "dorsal_r",
        };

        var deviceSpecific = topology.Zones
            .Select(z => z.Id)
            .Where(id => !portable.Contains(id))
            .ToList();

        deviceSpecific.Should().OnlyContain(id => id.StartsWith("bhaptics_"),
            "non-portable zone IDs must use the bhaptics_ prefix to avoid colliding with other backends");
        deviceSpecific.Should().NotBeEmpty(
            "vest should expose at least one finer-than-quadrant zone");
    }

    [Fact]
    public void Vest_exposes_torso_group()
    {
        var topology = BhapticsZoneTopology.BuildVest();
        var torso = topology.Groups.Should().ContainSingle(g => g.Id == "torso").Subject;
        torso.ZoneIds.Should().Contain(new[]
        {
            "pectoral_l", "pectoral_r",
            "abdominal_l", "abdominal_r",
        });
    }

    [Theory]
    [InlineData(true, "arm_l")]
    [InlineData(false, "arm_r")]
    public void Sleeve_exposes_portable_arm_zone(bool isLeft, string expectedId)
    {
        var topology = BhapticsZoneTopology.BuildSleeve(isLeft);
        topology.Zones.Select(z => z.Id).Should().Contain(expectedId);
    }

    [Theory]
    [InlineData(true, "_l")]
    [InlineData(false, "_r")]
    public void Sleeve_device_specific_zones_carry_correct_suffix(bool isLeft, string expectedSuffix)
    {
        var topology = BhapticsZoneTopology.BuildSleeve(isLeft);
        var deviceSpecific = topology.Zones
            .Select(z => z.Id)
            .Where(id => id.StartsWith("bhaptics_sleeve_"))
            .ToList();

        deviceSpecific.Should().NotBeEmpty();
        deviceSpecific.Should().OnlyContain(id => id.EndsWith(expectedSuffix));
    }

    [Theory]
    [InlineData(true, "foot_l")]
    [InlineData(false, "foot_r")]
    public void Feet_exposes_portable_foot_zone(bool isLeft, string expectedId)
    {
        var topology = BhapticsZoneTopology.BuildFeet(isLeft);
        topology.Zones.Select(z => z.Id).Should().Contain(expectedId);
    }

    [Theory]
    [InlineData(true, "_l")]
    [InlineData(false, "_r")]
    public void Feet_device_specific_zones_carry_correct_suffix(bool isLeft, string expectedSuffix)
    {
        var topology = BhapticsZoneTopology.BuildFeet(isLeft);
        var deviceSpecific = topology.Zones
            .Select(z => z.Id)
            .Where(id => id.StartsWith("bhaptics_feet_"))
            .ToList();

        deviceSpecific.Should().NotBeEmpty();
        deviceSpecific.Should().OnlyContain(id => id.EndsWith(expectedSuffix));
    }
}

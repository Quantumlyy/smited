using FluentAssertions;
using Smited.Daemon.Backends;
using Xunit;

namespace Smited.Daemon.Tests.Backends;

/// <summary>
/// Pin every bHaptics zone-id → motor-index mapping. Hardware
/// verification (firing each zone against a real device) catches
/// physical-orientation bugs; these tests catch table-level
/// regressions (typos, off-by-ones, duplicated indices,
/// cross-backend zone IDs that silently lose meaning).
/// </summary>
public sealed class BhapticsMotorMapTests
{
    // ---- Vest ----

    [Theory]
    [InlineData("pectoral_l", new[] { 0, 1, 4, 5 })]
    [InlineData("pectoral_r", new[] { 2, 3, 6, 7 })]
    [InlineData("abdominal_l", new[] { 8, 9, 12, 13, 16, 17 })]
    [InlineData("abdominal_r", new[] { 10, 11, 14, 15, 18, 19 })]
    [InlineData("dorsal_l", new[] { 20, 21, 24, 25 })]
    [InlineData("dorsal_r", new[] { 22, 23, 26, 27 })]
    [InlineData("lumbar_l", new[] { 28, 29, 32, 33, 36, 37 })]
    [InlineData("lumbar_r", new[] { 30, 31, 34, 35, 38, 39 })]
    [InlineData("bhaptics_vest_chest_high_l", new[] { 0, 1 })]
    [InlineData("bhaptics_vest_chest_high_r", new[] { 2, 3 })]
    [InlineData("bhaptics_vest_back_high_l", new[] { 20, 21 })]
    [InlineData("bhaptics_vest_back_high_r", new[] { 22, 23 })]
    public void Vest_zone_resolves_to_expected_motors(string zoneId, int[] expected)
    {
        BhapticsMotorMap.VestMotorsForZone(zoneId).Should().Equal(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("arm_l")]                       // sleeve zone shouldn't resolve on vest
    [InlineData("bhaptics_vest_unknown")]
    public void Vest_unknown_zone_resolves_to_empty(string zoneId)
    {
        BhapticsMotorMap.VestMotorsForZone(zoneId).Should().BeEmpty();
    }

    [Fact]
    public void Vest_cross_backend_zones_partition_all_40_motors_exactly_once()
    {
        var crossBackendZones = new[]
        {
            "pectoral_l", "pectoral_r",
            "abdominal_l", "abdominal_r",
            "dorsal_l", "dorsal_r",
            "lumbar_l", "lumbar_r",
        };

        var allMotors = crossBackendZones
            .SelectMany(BhapticsMotorMap.VestMotorsForZone)
            .ToList();

        allMotors.Should().HaveCount(40,
            "every TactSuit X40 motor must belong to exactly one cross-backend zone");
        allMotors.Distinct().Should().HaveCount(40,
            "no motor should appear in two cross-backend zones");
        allMotors.Min().Should().Be(0);
        allMotors.Max().Should().Be(39);
    }

    // ---- Sleeve ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Sleeve_arm_zone_covers_all_six_motors(bool isLeft)
    {
        var arm = isLeft ? "arm_l" : "arm_r";
        BhapticsMotorMap.SleeveMotorsForZone(arm, isLeft)
            .Should().Equal(0, 1, 2, 3, 4, 5);
    }

    [Theory]
    [InlineData(true, "bhaptics_sleeve_wrist_l", new[] { 0 })]
    [InlineData(true, "bhaptics_sleeve_forearm_l", new[] { 1, 2 })]
    [InlineData(true, "bhaptics_sleeve_elbow_l", new[] { 3 })]
    [InlineData(true, "bhaptics_sleeve_bicep_l", new[] { 4, 5 })]
    [InlineData(false, "bhaptics_sleeve_wrist_r", new[] { 0 })]
    [InlineData(false, "bhaptics_sleeve_forearm_r", new[] { 1, 2 })]
    [InlineData(false, "bhaptics_sleeve_elbow_r", new[] { 3 })]
    [InlineData(false, "bhaptics_sleeve_bicep_r", new[] { 4, 5 })]
    public void Sleeve_device_specific_zone_resolves_per_side(bool isLeft, string zoneId, int[] expected)
    {
        BhapticsMotorMap.SleeveMotorsForZone(zoneId, isLeft).Should().Equal(expected);
    }

    [Theory]
    [InlineData(true, "arm_r")]
    [InlineData(true, "bhaptics_sleeve_wrist_r")]
    [InlineData(false, "arm_l")]
    [InlineData(false, "bhaptics_sleeve_wrist_l")]
    public void Sleeve_wrong_side_zone_resolves_to_empty(bool isLeft, string zoneId)
    {
        BhapticsMotorMap.SleeveMotorsForZone(zoneId, isLeft).Should().BeEmpty();
    }

    // ---- Feet ----

    [Theory]
    [InlineData(true, "foot_l", new[] { 0, 1, 2 })]
    [InlineData(true, "bhaptics_feet_heel_l", new[] { 0 })]
    [InlineData(true, "bhaptics_feet_arch_l", new[] { 1 })]
    [InlineData(true, "bhaptics_feet_toes_l", new[] { 2 })]
    [InlineData(false, "foot_r", new[] { 0, 1, 2 })]
    [InlineData(false, "bhaptics_feet_heel_r", new[] { 0 })]
    [InlineData(false, "bhaptics_feet_arch_r", new[] { 1 })]
    [InlineData(false, "bhaptics_feet_toes_r", new[] { 2 })]
    public void Feet_zone_resolves_per_side(bool isLeft, string zoneId, int[] expected)
    {
        BhapticsMotorMap.FeetMotorsForZone(zoneId, isLeft).Should().Equal(expected);
    }

    [Theory]
    [InlineData(true, "foot_r")]
    [InlineData(false, "foot_l")]
    public void Feet_wrong_side_zone_resolves_to_empty(bool isLeft, string zoneId)
    {
        BhapticsMotorMap.FeetMotorsForZone(zoneId, isLeft).Should().BeEmpty();
    }
}

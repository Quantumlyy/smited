using FluentAssertions;
using Smited.Daemon.Bhaptics;
using Smited.Daemon.Bhaptics.WebSocket;
using Xunit;

namespace Smited.Daemon.Tests.Bhaptics;

public class ZoneIndexMapTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(19)]
    public void Resolve_for_each_vest_front_zone_returns_VestFront_with_motor_index(int index)
    {
        var (position, motor) = ZoneIndexMap.Resolve($"vest_front_{index}");
        position.Should().Be(Position.VestFront);
        motor.Should().Be(index);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    [InlineData(19)]
    public void Resolve_for_each_vest_back_zone_returns_VestBack_with_motor_index(int index)
    {
        var (position, motor) = ZoneIndexMap.Resolve($"vest_back_{index}");
        position.Should().Be(Position.VestBack);
        motor.Should().Be(index);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Resolve_for_each_glove_zone_returns_Glove_position(int index)
    {
        var (lp, lm) = ZoneIndexMap.Resolve($"glove_l_{index}");
        lp.Should().Be(Position.GloveL);
        lm.Should().Be(index);

        var (rp, rm) = ZoneIndexMap.Resolve($"glove_r_{index}");
        rp.Should().Be(Position.GloveR);
        rm.Should().Be(index);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Resolve_for_each_sleeve_zone_returns_Forearm_position(int index)
    {
        var (lp, lm) = ZoneIndexMap.Resolve($"arm_l_{index}");
        lp.Should().Be(Position.ForearmL);
        lm.Should().Be(index);

        var (rp, rm) = ZoneIndexMap.Resolve($"arm_r_{index}");
        rp.Should().Be(Position.ForearmR);
        rm.Should().Be(index);
    }

    [Fact]
    public void Resolve_throws_for_unknown_zone()
    {
        var act = () => ZoneIndexMap.Resolve("not_a_zone");
        act.Should().Throw<ArgumentException>().WithMessage("*not_a_zone*");
    }

    [Fact]
    public void EnclosingPosition_returns_VestFront_when_only_front_zones()
    {
        ZoneIndexMap.EnclosingPosition(new[] { "vest_front_0", "vest_front_5" })
            .Should().Be(Position.VestFront);
    }

    [Fact]
    public void EnclosingPosition_returns_VestBack_when_only_back_zones()
    {
        ZoneIndexMap.EnclosingPosition(new[] { "vest_back_0", "vest_back_19" })
            .Should().Be(Position.VestBack);
    }

    [Fact]
    public void EnclosingPosition_returns_Vest_when_zones_span_both_halves()
    {
        ZoneIndexMap.EnclosingPosition(new[] { "vest_front_0", "vest_back_0" })
            .Should().Be(Position.Vest);
    }

    [Fact]
    public void EnclosingPosition_for_glove_zones_returns_GloveL_or_GloveR()
    {
        ZoneIndexMap.EnclosingPosition(new[] { "glove_l_0", "glove_l_3" })
            .Should().Be(Position.GloveL);
        ZoneIndexMap.EnclosingPosition(new[] { "glove_r_0", "glove_r_5" })
            .Should().Be(Position.GloveR);
    }

    [Fact]
    public void EnclosingPosition_throws_for_empty_input()
    {
        var act = () => ZoneIndexMap.EnclosingPosition(Array.Empty<string>());
        act.Should().Throw<ArgumentException>();
    }
}

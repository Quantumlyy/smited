// Excluded from compile on non-Windows hosts via the test csproj's
// conditional <Compile Remove>. Imports the OWOGame Muscle enum which is
// only available in the Windows-only OWO NuGet package.

using FluentAssertions;
using OWOGame;
using Smited.Daemon.Owo;
using Xunit;

namespace Smited.Daemon.Tests.Backends;

public class OwoMuscleMapTests
{
    [Theory]
    [InlineData("pectoral_l", Muscle.Pectoral_L)]
    [InlineData("pectoral_r", Muscle.Pectoral_R)]
    [InlineData("abdominal_l", Muscle.Abdominal_L)]
    [InlineData("abdominal_r", Muscle.Abdominal_R)]
    [InlineData("lumbar_l", Muscle.Lumbar_L)]
    [InlineData("lumbar_r", Muscle.Lumbar_R)]
    [InlineData("dorsal_l", Muscle.Dorsal_L)]
    [InlineData("dorsal_r", Muscle.Dorsal_R)]
    [InlineData("arm_l", Muscle.Arm_L)]
    [InlineData("arm_r", Muscle.Arm_R)]
    public void Resolve_returns_the_matching_muscle(string zoneId, Muscle expected)
    {
        OwoMuscleMap.Resolve(zoneId).Should().Be(expected);
    }

    [Fact]
    public void Resolve_is_case_insensitive()
    {
        OwoMuscleMap.Resolve("PECTORAL_L").Should().Be(Muscle.Pectoral_L);
        OwoMuscleMap.Resolve("Arm_R").Should().Be(Muscle.Arm_R);
    }

    [Fact]
    public void Resolve_throws_for_unknown_zone()
    {
        var act = () => OwoMuscleMap.Resolve("not_a_zone");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not_a_zone*");
    }

    [Fact]
    public void Resolve_for_a_list_returns_muscles_in_order()
    {
        var zones = new[] { "pectoral_l", "arm_r", "dorsal_l" };

        var muscles = OwoMuscleMap.Resolve(zones);

        muscles.Should().Equal(Muscle.Pectoral_L, Muscle.Arm_R, Muscle.Dorsal_L);
    }
}

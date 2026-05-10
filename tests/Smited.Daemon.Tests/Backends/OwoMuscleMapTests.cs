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
    // Muscle is a struct, so its values can't appear directly in
    // [InlineData] (attribute args must be constants/typeof/arrays).
    // MemberData feeds them via runtime references instead.
    public static IEnumerable<object[]> ZoneToMuscle => new[]
    {
        new object[] { "pectoral_l", Muscle.Pectoral_L },
        new object[] { "pectoral_r", Muscle.Pectoral_R },
        new object[] { "abdominal_l", Muscle.Abdominal_L },
        new object[] { "abdominal_r", Muscle.Abdominal_R },
        new object[] { "lumbar_l", Muscle.Lumbar_L },
        new object[] { "lumbar_r", Muscle.Lumbar_R },
        new object[] { "dorsal_l", Muscle.Dorsal_L },
        new object[] { "dorsal_r", Muscle.Dorsal_R },
        new object[] { "arm_l", Muscle.Arm_L },
        new object[] { "arm_r", Muscle.Arm_R },
    };

    [Theory]
    [MemberData(nameof(ZoneToMuscle))]
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

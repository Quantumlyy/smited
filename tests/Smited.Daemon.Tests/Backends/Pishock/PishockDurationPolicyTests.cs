using FluentAssertions;
using Smited.Daemon.Pishock;
using Smited.Daemon.Pishock.Internal;
using Xunit;

namespace Smited.Daemon.Tests.Backends.Pishock;

public class PishockDurationPolicyTests
{
    [Fact]
    public void Lan_mode_passes_milliseconds_through_unchanged()
    {
        PishockDurationPolicy.Effective(PishockTransportMode.Lan, TimeSpan.FromMilliseconds(250))
            .Should().Be(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public void Cloud_mode_rounds_sub_second_authored_up_to_one_second()
    {
        PishockDurationPolicy.Effective(PishockTransportMode.Cloud, TimeSpan.FromMilliseconds(50))
            .Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Cloud_mode_rounds_multi_second_authored_up_to_next_whole_second()
    {
        PishockDurationPolicy.Effective(PishockTransportMode.Cloud, TimeSpan.FromMilliseconds(2200))
            .Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Cloud_mode_zero_authored_stays_zero_not_rounded_to_one_second()
    {
        // Schema allows duration=0; the backend treats that as a no-op
        // (don't fire). Math.Max(1, ...) was rounding zero up to 1s
        // and the cloud client would then fire a 1-second pulse on a
        // microsensation the user authored as silent.
        PishockDurationPolicy.Effective(PishockTransportMode.Cloud, TimeSpan.Zero)
            .Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Lan_mode_zero_authored_stays_zero()
    {
        PishockDurationPolicy.Effective(PishockTransportMode.Lan, TimeSpan.Zero)
            .Should().Be(TimeSpan.Zero);
    }
}

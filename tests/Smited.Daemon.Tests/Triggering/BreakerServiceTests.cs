using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Smited.Daemon.Triggering;
using Xunit;

namespace Smited.Daemon.Tests.Triggering;

public class BreakerServiceTests
{
    [Fact]
    public void Trip_then_Rearm_returns_to_untripped()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero));
        var breaker = new BreakerService(time);

        breaker.Trip("test");
        breaker.IsTripped.Should().BeTrue();
        breaker.TripReason.Should().Be("test");
        breaker.TrippedAt.Should().Be(time.GetUtcNow());

        breaker.Rearm();
        breaker.IsTripped.Should().BeFalse();
        breaker.TrippedAt.Should().BeNull();
        breaker.TripReason.Should().BeNull();
    }

    [Fact]
    public void StateChanged_fires_on_Trip_and_Rearm()
    {
        var time = new FakeTimeProvider();
        var breaker = new BreakerService(time);
        var states = new List<BreakerState>();
        breaker.StateChanged += s => states.Add(s);

        breaker.Trip("test");
        breaker.Rearm();

        states.Should().HaveCount(2);
        states[0].IsTripped.Should().BeTrue();
        states[1].IsTripped.Should().BeFalse();
    }

    [Fact]
    public void Rearm_on_untripped_breaker_does_not_fire_StateChanged()
    {
        var time = new FakeTimeProvider();
        var breaker = new BreakerService(time);
        var fired = false;
        breaker.StateChanged += _ => fired = true;

        breaker.Rearm();

        fired.Should().BeFalse();
    }
}

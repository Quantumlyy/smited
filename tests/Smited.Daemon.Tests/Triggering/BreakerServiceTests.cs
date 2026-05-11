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

    [Fact]
    public void Trip_swallows_subscriber_exception_and_still_records_state()
    {
        // SmitedActionService.PanicAsync calls Trip BEFORE StopAsync.
        // If a Trip subscriber throws, the exception must NOT propagate
        // out of Trip: otherwise the panic stop is skipped and active
        // sensations keep playing. The state must also be set (so
        // future triggers reject) and the call must return normally.
        var time = new FakeTimeProvider();
        var breaker = new BreakerService(time);
        breaker.StateChanged += _ => throw new InvalidOperationException("bad subscriber");

        var act = () => breaker.Trip("test");

        act.Should().NotThrow();
        breaker.IsTripped.Should().BeTrue();
        breaker.TripReason.Should().Be("test");
    }

    [Fact]
    public void Trip_continues_invoking_remaining_subscribers_after_a_throwing_one()
    {
        // Per-handler isolation: a single bad subscriber must not
        // suppress the others. The order of subscriber invocation is
        // delegate-registration order (Delegate.GetInvocationList), so
        // registering the thrower first proves the loop continues
        // after a catch.
        var time = new FakeTimeProvider();
        var breaker = new BreakerService(time);
        var ok = 0;
        breaker.StateChanged += _ => throw new InvalidOperationException("bad subscriber");
        breaker.StateChanged += _ => Interlocked.Increment(ref ok);

        breaker.Trip("test");

        ok.Should().Be(1);
    }

    [Fact]
    public void Rearm_swallows_subscriber_exception_and_still_records_state()
    {
        var time = new FakeTimeProvider();
        var breaker = new BreakerService(time);
        breaker.Trip("test");
        breaker.StateChanged += _ => throw new InvalidOperationException("bad subscriber");

        var act = () => breaker.Rearm();

        act.Should().NotThrow();
        breaker.IsTripped.Should().BeFalse();
    }
}

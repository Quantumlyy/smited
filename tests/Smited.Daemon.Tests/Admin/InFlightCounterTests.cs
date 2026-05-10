using FluentAssertions;
using Smited.Daemon.Admin.Services;
using Xunit;

namespace Smited.Daemon.Tests.Admin;

/// <summary>
/// Round-N+2 fix #2 lock-in. <see cref="InFlightCounter"/> exists to
/// solve the "subscribed mid-sensation, counter drifts negative
/// forever" bug: the previous round's render-time
/// <c>Math.Max(0, ...)</c> hid the negative value but didn't repair
/// the underlying drift, so the next real <c>SensationStarted</c>
/// moved the counter from -1 to 0 instead of 0 to 1, leaving the
/// card permanently one behind.
/// </summary>
public class InFlightCounterTests
{
    [Fact]
    public void Decrement_when_already_zero_stays_at_zero()
    {
        var counter = new InFlightCounter();

        var result = counter.DecrementClampedAtZero();

        result.Should().Be(0);
        counter.Current.Should().Be(0);
    }

    /// <summary>
    /// Reproduces the subscribe-mid-sensation bug. The counter starts
    /// at zero (the component just attached its subscription); a
    /// <c>SensationCompleted</c> for a sensation that started before
    /// the subscription arrives. Pre-fix the counter would land at -1
    /// and the next <c>SensationStarted</c> would only push it to 0.
    /// Post-fix the counter stays at 0 and the next start lands at 1.
    /// </summary>
    [Fact]
    public void Subscribe_mid_sensation_self_heals_on_next_started()
    {
        var counter = new InFlightCounter();

        // Decrement from 0 (would have been -1 with plain Interlocked).
        counter.DecrementClampedAtZero();
        counter.Current.Should().Be(0, "decrement at zero must clamp, not go negative");

        // Next real Started → counter goes to 1, accurately reflecting
        // the new sensation. Pre-fix this would have been 0 because
        // the previous decrement left the underlying field at -1.
        counter.Increment();
        counter.Current.Should().Be(1, "next start must reflect a real in-flight sensation");
    }

    [Fact]
    public void Concurrent_decrements_terminate_at_zero()
    {
        // CAS-loop sanity: 1000 concurrent decrements from 100 settle
        // at zero. Pre-fix (plain Interlocked.Decrement) would have
        // landed at -900.
        var counter = new InFlightCounter();
        for (var i = 0; i < 100; i++) counter.Increment();
        counter.Current.Should().Be(100);

        Parallel.For(0, 1000, _ => counter.DecrementClampedAtZero());

        counter.Current.Should().Be(0);
    }

    [Fact]
    public void Increment_followed_by_decrement_pair_returns_to_starting_value()
    {
        var counter = new InFlightCounter();

        counter.Increment();
        counter.Increment();
        counter.Increment();
        counter.Current.Should().Be(3);

        counter.DecrementClampedAtZero();
        counter.Current.Should().Be(2);
        counter.DecrementClampedAtZero();
        counter.Current.Should().Be(1);
        counter.DecrementClampedAtZero();
        counter.Current.Should().Be(0);

        // Extra decrement past zero stays at zero.
        counter.DecrementClampedAtZero();
        counter.Current.Should().Be(0);
    }

    [Fact]
    public void Increment_returns_new_value()
    {
        var counter = new InFlightCounter();

        counter.Increment().Should().Be(1);
        counter.Increment().Should().Be(2);
        counter.Increment().Should().Be(3);
    }

    [Fact]
    public void DecrementClampedAtZero_returns_new_value()
    {
        var counter = new InFlightCounter();
        counter.Increment();
        counter.Increment();

        counter.DecrementClampedAtZero().Should().Be(1);
        counter.DecrementClampedAtZero().Should().Be(0);
        counter.DecrementClampedAtZero().Should().Be(0); // clamped
    }
}

using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Smited.Daemon.Pishock.Internal;
using Xunit;

namespace Smited.Daemon.Tests.Backends.Pishock;

public class TokenBucketTests
{
    private static FakeTimeProvider NewTime() =>
        new(new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void New_bucket_starts_full_and_grants_capacity_consecutive_consumes()
    {
        var time = NewTime();
        var bucket = new TokenBucket(capacity: 3, refillPerSecond: 1, time);

        bucket.TryConsume().Should().BeTrue();
        bucket.TryConsume().Should().BeTrue();
        bucket.TryConsume().Should().BeTrue();
    }

    [Fact]
    public void Consuming_past_capacity_returns_false_until_refill()
    {
        var time = NewTime();
        var bucket = new TokenBucket(capacity: 3, refillPerSecond: 1, time);

        bucket.TryConsume(); bucket.TryConsume(); bucket.TryConsume();

        bucket.TryConsume().Should().BeFalse();
    }

    [Fact]
    public void Tokens_refill_at_configured_rate()
    {
        var time = NewTime();
        var bucket = new TokenBucket(capacity: 3, refillPerSecond: 2, time);

        bucket.TryConsume(); bucket.TryConsume(); bucket.TryConsume();
        bucket.TryConsume().Should().BeFalse();

        // Half a second at 2 ops/s → exactly one token refilled.
        time.Advance(TimeSpan.FromMilliseconds(500));

        bucket.TryConsume().Should().BeTrue();
        bucket.TryConsume().Should().BeFalse();
    }

    [Fact]
    public void Refill_is_capped_at_capacity()
    {
        var time = NewTime();
        var bucket = new TokenBucket(capacity: 3, refillPerSecond: 1, time);

        // Drain the bucket, then idle long enough that an unbounded
        // refill would accumulate well past capacity.
        bucket.TryConsume(); bucket.TryConsume(); bucket.TryConsume();
        time.Advance(TimeSpan.FromSeconds(60));

        // We should still only get capacity-many consecutive consumes;
        // the 4th must fail because tokens cap at capacity, not at the
        // 60 tokens an unbounded counter would have produced.
        bucket.TryConsume().Should().BeTrue();
        bucket.TryConsume().Should().BeTrue();
        bucket.TryConsume().Should().BeTrue();
        bucket.TryConsume().Should().BeFalse();
    }

    [Fact]
    public void TryConsume_with_count_atomic_succeeds_when_enough_tokens()
    {
        var time = NewTime();
        var bucket = new TokenBucket(capacity: 5, refillPerSecond: 1, time);

        bucket.TryConsume(3).Should().BeTrue();
        // 5 - 3 = 2 remaining.
        bucket.TryConsume(2).Should().BeTrue();
        bucket.TryConsume(1).Should().BeFalse();
    }

    [Fact]
    public void TryConsume_with_count_atomic_does_not_leak_tokens_on_failure()
    {
        // The PiShock backend pre-allocates one token per microsensation
        // before starting a multi-pulse trigger. A non-atomic loop would
        // consume tokens up to the failure point, leaking them — a
        // 3-pulse trigger with 2 tokens available would burn 2 tokens
        // and reject. The next trigger that needed those tokens would
        // also be rejected even though the bucket should still have 2.
        var time = NewTime();
        var bucket = new TokenBucket(capacity: 2, refillPerSecond: 1, time);

        bucket.TryConsume(3).Should().BeFalse();

        // Atomic semantic: failure leaves the bucket exactly as it was.
        bucket.TryConsume(2).Should().BeTrue();
    }

    [Fact]
    public void Partial_refill_below_one_token_does_not_grant_a_consume()
    {
        var time = NewTime();
        var bucket = new TokenBucket(capacity: 1, refillPerSecond: 1, time);

        bucket.TryConsume();
        bucket.TryConsume().Should().BeFalse();

        // 0.4s elapsed → 0.4 tokens accrued, still below the integer
        // threshold required to grant a consume.
        time.Advance(TimeSpan.FromMilliseconds(400));
        bucket.TryConsume().Should().BeFalse();

        time.Advance(TimeSpan.FromMilliseconds(700));
        bucket.TryConsume().Should().BeTrue();
    }
}

namespace Smited.Daemon.Pishock.Internal;

/// <summary>
/// Per-descriptor rate limiter. Each <see cref="TryConsume"/> attempt
/// either succeeds and removes one token, or fails because the bucket
/// is empty. Tokens accrue at <c>refillPerSecond</c> and saturate at
/// <c>capacity</c>.
/// </summary>
/// <remarks>
/// <para>
/// All time reads go through <see cref="TimeProvider.GetTimestamp"/> +
/// <see cref="TimeProvider.GetElapsedTime(long, long)"/>, so a
/// <c>FakeTimeProvider</c> handed in by tests can fast-forward the
/// clock and exercise the refill logic without <c>Thread.Sleep</c>.
/// </para>
/// <para>
/// A simple <c>lock</c> is sufficient: contention on a single shocker's
/// bucket is non-existent in practice — at the default refill rate of
/// 1 op/s the lock is held for sub-microsecond intervals between
/// trigger calls that are themselves seconds apart.
/// </para>
/// </remarks>
internal sealed class TokenBucket
{
    private readonly int _capacity;
    private readonly double _refillPerSecond;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private double _tokens;
    private long _lastRefillTimestamp;

    public TokenBucket(int capacity, int refillPerSecond, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(refillPerSecond, 1);

        _capacity = capacity;
        _refillPerSecond = refillPerSecond;
        _time = time;
        _tokens = capacity;
        _lastRefillTimestamp = time.GetTimestamp();
    }

    /// <summary>
    /// Attempts to consume <paramref name="count"/> tokens atomically:
    /// either all <paramref name="count"/> tokens are consumed and the
    /// method returns <c>true</c>, or the bucket is unchanged and the
    /// method returns <c>false</c>. Used by the PiShock backends to
    /// pre-allocate one token per microsensation in a multi-pulse
    /// trigger so partial failure can't leak tokens into the bucket's
    /// state for the next caller to inherit.
    /// </summary>
    public bool TryConsume(int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        lock (_lock)
        {
            Refill();
            if (_tokens >= count)
            {
                _tokens -= count;
                return true;
            }
            return false;
        }
    }

    private void Refill()
    {
        var now = _time.GetTimestamp();
        var elapsed = _time.GetElapsedTime(_lastRefillTimestamp, now);
        var refilled = elapsed.TotalSeconds * _refillPerSecond;
        if (refilled > 0)
        {
            _tokens = Math.Min(_capacity, _tokens + refilled);
            _lastRefillTimestamp = now;
        }
    }
}

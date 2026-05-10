namespace Smited.Daemon.Admin.Services;

/// <summary>
/// Thread-safe counter for the per-backend in-flight sensation count
/// rendered on <c>BackendCard</c>. Decrement clamps at zero so a
/// component that subscribes mid-sensation — i.e. after a
/// <c>SensationStarted</c> was already emitted but before its paired
/// <c>SensationCompleted</c> — self-heals on the next real
/// <c>SensationStarted</c> rather than persisting an offset of -1
/// indefinitely.
/// </summary>
/// <remarks>
/// Round-N+1 added a render-time <c>Math.Max(0, ...)</c> clamp on the
/// underlying <c>int</c>. That hides negative values from the user but
/// doesn't repair the underlying drift; subsequent decrements stay
/// negative, and the next real increment moves -1 to 0 instead of 0
/// to 1. Round-N+2 moves the clamp to write-time so the underlying
/// state stays correct.
///
/// Decrement uses a CAS loop because the channel reader processes events
/// one at a time, but <see cref="Current"/> can in principle be read
/// concurrently by the renderer. In practice the channel-reader-is-
/// single-threaded contract makes a write-write race vanishingly
/// unlikely; the CAS is the correct shape and costs nothing on the
/// uncontended path.
/// </remarks>
internal sealed class InFlightCounter
{
    private int _value;

    public int Current => Volatile.Read(ref _value);

    public int Increment() => Interlocked.Increment(ref _value);

    /// <summary>
    /// Atomic decrement that clamps at zero. Returns the new value
    /// (always &gt;= 0).
    /// </summary>
    public int DecrementClampedAtZero()
    {
        while (true)
        {
            var current = Volatile.Read(ref _value);
            if (current <= 0) return 0;

            var desired = current - 1;
            if (Interlocked.CompareExchange(ref _value, desired, current) == current)
            {
                return desired;
            }
            // Lost the race; retry.
        }
    }
}

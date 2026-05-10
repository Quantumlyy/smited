namespace Smited.Daemon.Admin.Services;

/// <summary>
/// Singleton counter, incremented each time the admin UI panic button fires.
/// Exists so the button label can show "Panic fired N times this session";
/// resets when the daemon restarts.
/// </summary>
internal sealed class PanicCounter
{
    private long _count;

    public long Count => Interlocked.Read(ref _count);

    public long Increment() => Interlocked.Increment(ref _count);
}

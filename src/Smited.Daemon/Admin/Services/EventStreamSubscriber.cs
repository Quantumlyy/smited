using System.Threading.Channels;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Events;

namespace Smited.Daemon.Admin.Services;

/// <summary>
/// Per-Blazor-circuit subscription to the daemon <see cref="EventBus"/>.
/// Each component that wants live updates injects this and calls
/// <see cref="StreamAsync"/> inside a background task. Disposing the
/// subscriber cancels the stream and releases the underlying subscription.
/// </summary>
internal sealed class EventStreamSubscriber : IAsyncDisposable
{
    private readonly EventBus _bus;
    private EventBus.Subscription? _subscription;

    public EventStreamSubscriber(EventBus bus) => _bus = bus;

    public IAsyncEnumerable<BackendEvent> StreamAsync(
        int bufferCapacity = 256,
        BoundedChannelFullMode fullMode = BoundedChannelFullMode.DropOldest,
        CancellationToken ct = default)
    {
        _subscription ??= _bus.Subscribe(bufferCapacity, fullMode);
        return _subscription.Reader.ReadAllAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_subscription is not null)
        {
            await _subscription.DisposeAsync().ConfigureAwait(false);
            _subscription = null;
        }
    }
}

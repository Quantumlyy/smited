using System.Threading.Channels;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Events;

namespace Smited.Daemon.Admin.Services;

/// <summary>
/// Per-component-instance wrapper around an <see cref="EventBus"/>
/// subscription. Registered as <c>Transient</c> so each Blazor
/// component that injects it gets its own underlying
/// <see cref="EventBus.Subscription"/>.
/// </summary>
/// <remarks>
/// Channel readers are single-consumer; if multiple components shared
/// one subscription (i.e. <c>AddScoped</c>), each event would be
/// delivered to whichever component's <c>await foreach</c> won the read
/// race, and the others would miss it. Transient lifecycle avoids this
/// by giving each consumer their own subscription on the bus.
///
/// Components must dispose this subscriber (typically via
/// <c>IAsyncDisposable</c> on the component) to release the underlying
/// <see cref="EventBus.Subscription"/>.
/// </remarks>
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

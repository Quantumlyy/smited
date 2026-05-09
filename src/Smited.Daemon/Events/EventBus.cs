using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Smited.Daemon.Backends.Internal;

namespace Smited.Daemon.Events;

/// <summary>
/// In-process pub/sub for daemon-wide backend events. Backends and the
/// registry publish to it; the gRPC <c>SubscribeEvents</c> handler subscribes
/// to it via <see cref="EventStream"/>. Each subscriber gets its own bounded
/// channel so a slow consumer can't back-pressure the bus or other consumers.
/// </summary>
/// <remarks>
/// Subscribers are required to use a drop-on-overflow <see cref="BoundedChannelFullMode"/>
/// (<c>DropOldest</c> or <c>DropNewest</c>). <c>Wait</c> is rejected because
/// <see cref="ChannelWriter{T}.TryWrite(T)"/> can't distinguish "buffer full"
/// from "channel closed" in that mode, which would cause the publisher to
/// erroneously evict slow subscribers; if you want the per-event-block
/// semantics that mode provides, write a different bus.
/// </remarks>
internal sealed class EventBus : IBackendEventSink
{
    private readonly ConcurrentDictionary<int, Subscriber> _subscribers = new();
    private readonly ILogger<EventBus> _logger;
    private int _nextSubscriberId;

    public EventBus(ILogger<EventBus> logger)
    {
        _logger = logger;
    }

    /// <summary>Number of currently-active subscribers. For diagnostics/tests.</summary>
    public int SubscriberCount => _subscribers.Count;

    public Subscription Subscribe(int bufferCapacity, BoundedChannelFullMode fullMode)
    {
        if (bufferCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferCapacity), "must be positive");
        }
        if (fullMode != BoundedChannelFullMode.DropOldest && fullMode != BoundedChannelFullMode.DropNewest)
        {
            throw new ArgumentOutOfRangeException(nameof(fullMode),
                $"EventBus requires a drop-on-overflow channel mode; got {fullMode}.");
        }

        var channel = Channel.CreateBounded<BackendEvent>(new BoundedChannelOptions(bufferCapacity)
        {
            FullMode = fullMode,
            SingleReader = true,
            SingleWriter = false,
        });

        var id = Interlocked.Increment(ref _nextSubscriberId);
        var subscriber = new Subscriber(id, channel);
        _subscribers[id] = subscriber;
        _logger.LogDebug("EventBus subscriber {Id} attached (capacity={Capacity}, mode={Mode})",
            id, bufferCapacity, fullMode);
        return new Subscription(subscriber, this);
    }

    public void Publish(BackendEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        foreach (var pair in _subscribers)
        {
            var sub = pair.Value;
            // For DropOldest/DropNewest the channel always accepts via
            // TryWrite (silently dropping per its FullMode policy).
            // TryWrite returns false only when the writer was completed —
            // i.e. the subscriber was disposed but we haven't cleaned up
            // the registry entry yet.
            if (!sub.Channel.Writer.TryWrite(evt))
            {
                if (_subscribers.TryRemove(pair.Key, out _))
                {
                    _logger.LogDebug("EventBus dropped completed subscriber {Id}", sub.Id);
                }
            }
        }
    }

    private void Unsubscribe(int id)
    {
        if (_subscribers.TryRemove(id, out var subscriber))
        {
            subscriber.Channel.Writer.TryComplete();
            _logger.LogDebug("EventBus subscriber {Id} detached", id);
        }
    }

    internal sealed record Subscriber(int Id, Channel<BackendEvent> Channel);

    /// <summary>
    /// Handle returned by <see cref="Subscribe"/>. Disposing the subscription
    /// removes the writer and completes the channel reader so any in-flight
    /// <c>ReadAllAsync</c> enumeration ends cleanly.
    /// </summary>
    public sealed class Subscription : IAsyncDisposable
    {
        private readonly Subscriber _subscriber;
        private readonly EventBus _bus;
        private bool _disposed;

        internal Subscription(Subscriber subscriber, EventBus bus)
        {
            _subscriber = subscriber;
            _bus = bus;
        }

        public ChannelReader<BackendEvent> Reader => _subscriber.Channel.Reader;

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                _bus.Unsubscribe(_subscriber.Id);
            }
            return ValueTask.CompletedTask;
        }
    }
}

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
internal sealed class EventBus : IBackendEventSink
{
    private readonly ConcurrentDictionary<int, ChannelWriter<BackendEvent>> _writers = new();
    private readonly ILogger<EventBus> _logger;
    private int _nextSubscriberId;

    public EventBus(ILogger<EventBus> logger)
    {
        _logger = logger;
    }

    /// <summary>Number of currently-active subscribers. For diagnostics/tests.</summary>
    public int SubscriberCount => _writers.Count;

    public Subscription Subscribe(int bufferCapacity, BoundedChannelFullMode fullMode)
    {
        if (bufferCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferCapacity), "must be positive");
        }

        var channel = Channel.CreateBounded<BackendEvent>(new BoundedChannelOptions(bufferCapacity)
        {
            FullMode = fullMode,
            SingleReader = true,
            SingleWriter = false,
        });

        var id = Interlocked.Increment(ref _nextSubscriberId);
        _writers[id] = channel.Writer;
        return new Subscription(id, channel.Reader, this);
    }

    public void Publish(BackendEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        foreach (var pair in _writers)
        {
            // DropOldest channels evict on overflow and TryWrite still returns true.
            // TryWrite returns false only when the channel is closed — that's a
            // disposed subscriber we missed cleaning up; drop it now.
            if (!pair.Value.TryWrite(evt))
            {
                _writers.TryRemove(pair.Key, out _);
            }
        }
    }

    private void Unsubscribe(int id)
    {
        if (_writers.TryRemove(id, out var writer))
        {
            writer.TryComplete();
        }
    }

    /// <summary>
    /// Handle returned by <see cref="Subscribe"/>. Disposing the subscription
    /// removes the writer and completes the channel reader so any in-flight
    /// <c>ReadAllAsync</c> enumeration ends cleanly.
    /// </summary>
    public sealed class Subscription : IAsyncDisposable
    {
        private readonly int _id;
        private readonly EventBus _bus;
        private bool _disposed;

        internal Subscription(int id, ChannelReader<BackendEvent> reader, EventBus bus)
        {
            _id = id;
            Reader = reader;
            _bus = bus;
        }

        public ChannelReader<BackendEvent> Reader { get; }

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                _bus.Unsubscribe(_id);
            }
            return ValueTask.CompletedTask;
        }
    }
}

using System.Collections.Concurrent;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Events;

namespace Smited.Daemon.Backends;

/// <summary>
/// Thread-safe registry of currently-connected backends, keyed by
/// <see cref="IHapticBackend.Id"/>. Publishes a
/// <see cref="BackendLifecycleEvent"/> on every register/deregister.
/// </summary>
internal sealed class BackendRegistry
{
    private readonly ConcurrentDictionary<string, IHapticBackend> _backends =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IBackendEventSink _eventSink;
    private readonly TimeProvider _time;

    public BackendRegistry(IBackendEventSink eventSink, TimeProvider time)
    {
        _eventSink = eventSink;
        _time = time;
    }

    public int Count => _backends.Count;

    public IReadOnlyCollection<IHapticBackend> All => _backends.Values.ToArray();

    public void Register(IHapticBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);

        if (!_backends.TryAdd(backend.Id, backend))
        {
            throw new InvalidOperationException(
                $"Backend '{backend.Id}' is already registered.");
        }

        _eventSink.Publish(new BackendLifecycleEvent(
            backend.Id,
            _time.GetUtcNow(),
            BackendLifecycleChange.Registered,
            BackendSummarySnapshot.Of(backend),
            Reason: null));
    }

    public bool Deregister(string id)
    {
        if (!_backends.TryRemove(id, out var backend))
        {
            return false;
        }

        _eventSink.Publish(new BackendLifecycleEvent(
            backend.Id,
            _time.GetUtcNow(),
            BackendLifecycleChange.Deregistered,
            BackendSummarySnapshot.Of(backend),
            Reason: null));

        return true;
    }

    public IHapticBackend? TryGet(string id) =>
        _backends.TryGetValue(id, out var backend) ? backend : null;

    public IReadOnlyCollection<IHapticBackend> WhereCapability(string tag) =>
        _backends.Values
            .Where(b => b.Capabilities.Contains(tag, StringComparer.OrdinalIgnoreCase))
            .ToArray();
}

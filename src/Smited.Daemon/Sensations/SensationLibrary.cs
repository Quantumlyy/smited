using System.Collections.Concurrent;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Events;

namespace Smited.Daemon.Sensations;

/// <summary>
/// Thread-safe in-memory store for <see cref="RegisteredSensation"/>
/// entries, keyed by <c>(BackendId, Name)</c>. Mutations publish a
/// <see cref="SensationRegistryChangedEvent"/> through the configured
/// <see cref="IBackendEventSink"/>.
/// </summary>
internal sealed class SensationLibrary
{
    private readonly ConcurrentDictionary<(string BackendId, string Name), RegisteredSensation> _store =
        new(BackendNameKeyComparer.Instance);

    private readonly IBackendEventSink _eventSink;
    private readonly TimeProvider _time;

    public SensationLibrary(IBackendEventSink eventSink, TimeProvider time)
    {
        _eventSink = eventSink;
        _time = time;
    }

    public int Count => _store.Count;

    /// <summary>
    /// Registers <paramref name="sensation"/>, optionally replacing an
    /// existing entry with the same <c>(BackendId, Name)</c>. Returns
    /// false when an entry already exists and <paramref name="overwrite"/>
    /// is false.
    /// </summary>
    public bool Register(RegisteredSensation sensation, bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(sensation);
        var key = (sensation.BackendId, sensation.Name);

        if (overwrite)
        {
            _store[key] = sensation;
        }
        else if (!_store.TryAdd(key, sensation))
        {
            return false;
        }

        _eventSink.Publish(new SensationRegistryChangedEvent(
            sensation.BackendId,
            _time.GetUtcNow(),
            sensation.Name,
            SensationRegistryChange.Registered));

        return true;
    }

    public bool Unregister(string backendId, string name)
    {
        if (!_store.TryRemove((backendId, name), out _))
        {
            return false;
        }

        _eventSink.Publish(new SensationRegistryChangedEvent(
            backendId,
            _time.GetUtcNow(),
            name,
            SensationRegistryChange.Unregistered));

        return true;
    }

    public RegisteredSensation? Get(string backendId, string name) =>
        _store.TryGetValue((backendId, name), out var sensation) ? sensation : null;

    /// <summary>
    /// Lists all registered sensations, optionally narrowed by backend id
    /// and/or by required tags. An entry must carry every tag in
    /// <paramref name="tags"/> to be included.
    /// </summary>
    public IReadOnlyList<RegisteredSensation> List(string? backendId, IReadOnlyList<string>? tags)
    {
        IEnumerable<RegisteredSensation> q = _store.Values;
        if (!string.IsNullOrEmpty(backendId))
        {
            q = q.Where(s => string.Equals(s.BackendId, backendId, StringComparison.OrdinalIgnoreCase));
        }
        if (tags is { Count: > 0 })
        {
            q = q.Where(s => tags.All(t => s.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)));
        }
        return q.ToArray();
    }

    private sealed class BackendNameKeyComparer : IEqualityComparer<(string BackendId, string Name)>
    {
        public static readonly BackendNameKeyComparer Instance = new();

        public bool Equals((string BackendId, string Name) x, (string BackendId, string Name) y) =>
            string.Equals(x.BackendId, y.BackendId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string BackendId, string Name) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.BackendId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
    }
}

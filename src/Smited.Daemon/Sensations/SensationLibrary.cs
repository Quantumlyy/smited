using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Configuration;
using Smited.Daemon.Events;

namespace Smited.Daemon.Sensations;

/// <summary>
/// Thread-safe in-memory store for <see cref="RegisteredSensation"/>
/// entries, keyed by <c>(BackendId, Name)</c>. Mutations publish a
/// <see cref="SensationRegistryChangedEvent"/> through the configured
/// <see cref="IBackendEventSink"/>. Runtime registrations made via
/// <see cref="RegisterAsync"/> are also persisted to disk under the
/// configured <c>LibraryRoot</c> so they survive across daemon restarts.
/// </summary>
internal sealed class SensationLibrary
{
    private readonly ConcurrentDictionary<(string BackendId, string Name), RegisteredSensation> _store =
        new(BackendNameKeyComparer.Instance);

    private readonly IBackendEventSink _eventSink;
    private readonly TimeProvider _time;
    private readonly SmitedOptions _options;

    public SensationLibrary(
        IBackendEventSink eventSink,
        TimeProvider time,
        IOptions<SmitedOptions> options)
    {
        _eventSink = eventSink;
        _time = time;
        _options = options.Value;
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
    /// Registers <paramref name="sensation"/> in the in-memory store and
    /// persists it to <c>LibraryRoot/<paramref name="backendKind"/>/<see cref="RegisteredSensation.Name"/>.json</c>
    /// so it survives a daemon restart. The on-disk format is symmetric
    /// with what <see cref="SensationLoader"/> reads at boot — authored
    /// and runtime-registered sensations are indistinguishable.
    /// </summary>
    /// <remarks>
    /// In-memory insertion happens first; the disk write is attempted
    /// after. If the disk write fails the in-memory entry is rolled back
    /// and the exception propagates. The in-memory store is the source of
    /// truth for in-flight runtime state, but disk is the source of truth
    /// for surviving a restart, and we don't want them to disagree.
    /// </remarks>
    public async Task<bool> RegisterAsync(
        RegisteredSensation sensation,
        string backendKind,
        bool overwrite,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sensation);
        ArgumentException.ThrowIfNullOrEmpty(backendKind);

        var key = (sensation.BackendId, sensation.Name);
        var previous = overwrite && _store.TryGetValue(key, out var existing) ? existing : null;

        if (overwrite)
        {
            _store[key] = sensation;
        }
        else if (!_store.TryAdd(key, sensation))
        {
            return false;
        }

        try
        {
            await WriteToDiskAsync(sensation, backendKind, ct).ConfigureAwait(false);
        }
        catch
        {
            // Rollback in-memory state to keep memory and disk consistent.
            if (previous is null)
            {
                _store.TryRemove(key, out _);
            }
            else
            {
                _store[key] = previous;
            }
            throw;
        }

        _eventSink.Publish(new SensationRegistryChangedEvent(
            sensation.BackendId,
            _time.GetUtcNow(),
            sensation.Name,
            SensationRegistryChange.Registered));

        return true;
    }

    /// <summary>
    /// Removes <paramref name="name"/> from the in-memory store and
    /// deletes its on-disk file under
    /// <c>LibraryRoot/<paramref name="backendKind"/>/<paramref name="name"/>.json</c>.
    /// Tolerant of an already-missing file.
    /// </summary>
    public async Task<bool> UnregisterAsync(
        string backendId,
        string backendKind,
        string name,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(backendKind);

        if (!_store.TryRemove((backendId, name), out _))
        {
            return false;
        }

        try
        {
            await DeleteFromDiskAsync(backendKind, name, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            // Already gone — in-memory removal was authoritative.
        }
        catch (DirectoryNotFoundException)
        {
            // Same — nothing to delete.
        }

        _eventSink.Publish(new SensationRegistryChangedEvent(
            backendId,
            _time.GetUtcNow(),
            name,
            SensationRegistryChange.Unregistered));

        return true;
    }

    private Task WriteToDiskAsync(RegisteredSensation sensation, string backendKind, CancellationToken ct)
    {
        var path = ResolveFilePath(backendKind, sensation.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var dto = SensationFileSerializer.ToDto(sensation, backendKind);
        var json = JsonSerializer.Serialize(dto, IndentedOptions);
        return File.WriteAllTextAsync(path, json, ct);
    }

    private Task DeleteFromDiskAsync(string backendKind, string name, CancellationToken ct)
    {
        var path = ResolveFilePath(backendKind, name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }

    private string ResolveFilePath(string backendKind, string name) =>
        Path.Combine(
            LibraryRootResolver.Resolve(_options.Sensations.LibraryRoot),
            backendKind,
            $"{name}.json");

    private static readonly JsonSerializerOptions IndentedOptions = new(SensationFileSerializer.Options)
    {
        WriteIndented = true,
    };

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

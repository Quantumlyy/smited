using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Events;

namespace Smited.Daemon.Tests.Fixtures;

internal sealed class RecordingEventSink : IBackendEventSink
{
    private readonly List<BackendEvent> _events = new();
    private readonly Lock _lock = new();

    public IReadOnlyList<BackendEvent> Events
    {
        get
        {
            lock (_lock)
            {
                return _events.ToArray();
            }
        }
    }

    public void Publish(BackendEvent evt)
    {
        lock (_lock)
        {
            _events.Add(evt);
        }
    }
}

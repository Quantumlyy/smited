using Smited.Daemon.Backends.Internal;

namespace Smited.Daemon.Events;

/// <summary>
/// Forward-declared seam between the registry and the EventBus. The bus
/// implements this in a later commit; until then, a no-op default keeps the
/// registry standalone-testable without dragging the channel infrastructure
/// in.
/// </summary>
internal interface IBackendEventSink
{
    void Publish(BackendEvent evt);
}

internal sealed class NoopBackendEventSink : IBackendEventSink
{
    public static readonly NoopBackendEventSink Instance = new();

    private NoopBackendEventSink() { }

    public void Publish(BackendEvent evt) { }
}

using Smited.Daemon.Backends.Internal;

namespace Smited.Daemon.Events;

/// <summary>
/// Seam between the registry / sensation library and the
/// <see cref="EventBus"/>. The bus implements this interface; DI binds
/// both registrations to the same singleton so callers that don't need
/// a full <c>EventBus</c> (just publish capability) can take this
/// narrower dependency.
/// </summary>
internal interface IBackendEventSink
{
    void Publish(BackendEvent evt);
}

/// <summary>
/// No-op sink used in tests that exercise the registry or sensation
/// library in isolation, without spinning up the full channel
/// infrastructure of <see cref="EventBus"/>.
/// </summary>
internal sealed class NoopBackendEventSink : IBackendEventSink
{
    public static readonly NoopBackendEventSink Instance = new();

    private NoopBackendEventSink() { }

    public void Publish(BackendEvent evt) { }
}

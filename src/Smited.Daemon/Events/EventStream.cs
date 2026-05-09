using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Configuration;
using Smited.V1;

namespace Smited.Daemon.Events;

/// <summary>
/// Adapter the gRPC <c>SubscribeEvents</c> handler calls to receive a
/// filtered, cancellable stream of backend events.
/// </summary>
internal sealed class EventStream
{
    private readonly EventBus _bus;
    private readonly SmitedOptions _options;
    private readonly ILogger<EventStream> _logger;

    public EventStream(
        EventBus bus,
        IOptions<SmitedOptions> options,
        ILogger<EventStream> logger)
    {
        _bus = bus;
        _options = options.Value;
        _logger = logger;
    }

    public async IAsyncEnumerable<BackendEvent> StreamAsync(
        SubscribeFilters filters,
        string peerLabel,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var fullMode = ResolveFullMode(_options.EventBus.SlowSubscriberPolicy);

        await using var sub = _bus.Subscribe(_options.EventBus.BufferCapacity, fullMode);

        _logger.LogDebug(
            "Event subscriber {Peer} attached (filters: kinds={Kinds}, backends={Backends})",
            peerLabel, filters.Kinds.Count, filters.BackendIds.Count);

        try
        {
            await foreach (var evt in sub.Reader.ReadAllAsync(ct))
            {
                if (filters.Kinds.Count > 0 && !filters.Kinds.Contains(KindOf(evt)))
                {
                    continue;
                }
                if (filters.BackendIds.Count > 0 && !filters.BackendIds.Contains(evt.BackendId))
                {
                    continue;
                }
                yield return evt;
            }
        }
        finally
        {
            _logger.LogDebug("Event subscriber {Peer} detached", peerLabel);
        }
    }

    internal static EventKind KindOf(BackendEvent evt) => evt switch
    {
        SensationStarted => EventKind.SensationStarted,
        SensationCompleted => EventKind.SensationCompleted,
        SensationCancelled => EventKind.SensationCancelled,
        BackendLifecycleEvent { Change: BackendLifecycleChange.Registered } => EventKind.BackendRegistered,
        BackendLifecycleEvent { Change: BackendLifecycleChange.Deregistered } => EventKind.BackendDeregistered,
        BackendLifecycleEvent { Change: BackendLifecycleChange.StatusChanged } => EventKind.BackendStatusChanged,
        CalibrationChangedEvent => EventKind.CalibrationChanged,
        SensationRegistryChangedEvent { Change: SensationRegistryChange.Registered } => EventKind.SensationRegistered,
        SensationRegistryChangedEvent { Change: SensationRegistryChange.Unregistered } => EventKind.SensationUnregistered,
        _ => EventKind.Unspecified,
    };

    internal static BoundedChannelFullMode ResolveFullMode(string policy) => policy switch
    {
        "drop_oldest" => BoundedChannelFullMode.DropOldest,
        "drop_newest" => BoundedChannelFullMode.DropNewest,
        "wait" => BoundedChannelFullMode.Wait,
        _ => BoundedChannelFullMode.DropOldest,
    };
}

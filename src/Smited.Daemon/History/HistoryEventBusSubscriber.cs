using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Events;

namespace Smited.Daemon.History;

/// <summary>
/// Subscribes to the daemon's <see cref="EventBus"/> at startup and
/// translates every <see cref="BackendEvent"/> into the corresponding
/// history row. Lifecycle events become <see cref="LifecycleRecord"/>;
/// backend register/deregister/status-change become
/// <see cref="BackendStateRecord"/>.
/// </summary>
/// <remarks>
/// New event kinds added to the bus are automatically reflected in
/// history without per-call-site wiring.
/// </remarks>
internal sealed class HistoryEventBusSubscriber : IHostedService
{
    private readonly EventBus _bus;
    private readonly IHistoryRecorder _recorder;
    private readonly ILogger<HistoryEventBusSubscriber> _log;

    private EventBus.Subscription? _subscription;
    private CancellationTokenSource? _cts;
    private Task? _consumer;

    public HistoryEventBusSubscriber(
        EventBus bus,
        IHistoryRecorder recorder,
        ILogger<HistoryEventBusSubscriber> log)
    {
        _bus = bus;
        _recorder = recorder;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _bus.Subscribe(2048, BoundedChannelFullMode.DropOldest);
        _cts = new CancellationTokenSource();
        _consumer = Task.Run(() => ConsumeAsync(_subscription.Reader, _cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            try { await _cts.CancelAsync(); } catch (ObjectDisposedException) { }
        }

        if (_subscription is not null)
        {
            try { await _subscription.DisposeAsync(); } catch { }
            _subscription = null;
        }

        if (_consumer is not null)
        {
            try
            {
                await _consumer.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }
    }

    private async Task ConsumeAsync(ChannelReader<BackendEvent> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var evt in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await RecordAsync(evt, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "History event subscriber crashed");
        }
    }

    private Task RecordAsync(BackendEvent evt, CancellationToken ct) => evt switch
    {
        SensationStarted s => _recorder.RecordLifecycleAsync(new LifecycleRecord
        {
            Timestamp = s.Timestamp,
            BackendId = s.BackendId,
            EventKind = nameof(SensationStarted),
            SensationId = s.SensationId,
            SensationName = s.SensationName,
            ClientTraceId = s.ClientTraceId,
        }, ct),
        SensationCompleted s => _recorder.RecordLifecycleAsync(new LifecycleRecord
        {
            Timestamp = s.Timestamp,
            BackendId = s.BackendId,
            EventKind = nameof(SensationCompleted),
            SensationId = s.SensationId,
            SensationName = s.SensationName,
            ClientTraceId = s.ClientTraceId,
        }, ct),
        SensationCancelled s => _recorder.RecordLifecycleAsync(new LifecycleRecord
        {
            Timestamp = s.Timestamp,
            BackendId = s.BackendId,
            EventKind = nameof(SensationCancelled),
            SensationId = s.SensationId,
            SensationName = s.SensationName,
            ClientTraceId = s.ClientTraceId,
            Reason = s.Reason,
        }, ct),
        BackendLifecycleEvent b => _recorder.RecordBackendStateAsync(new BackendStateRecord
        {
            Timestamp = b.Timestamp,
            BackendId = b.BackendId,
            Kind = b.Snapshot.Kind,
            DisplayName = b.Snapshot.DisplayName,
            Status = b.Snapshot.Status.ToString(),
            Event = b.Change switch
            {
                BackendLifecycleChange.Registered => "registered",
                BackendLifecycleChange.Deregistered => "deregistered",
                BackendLifecycleChange.StatusChanged => "status_changed",
                _ => "unknown",
            },
            Reason = b.Reason,
        }, ct),
        CalibrationChangedEvent c => _recorder.RecordLifecycleAsync(new LifecycleRecord
        {
            Timestamp = c.Timestamp,
            BackendId = c.BackendId,
            EventKind = "CalibrationChanged",
            Reason = c.NewState.Calibrated ? "calibrated" : "not_calibrated",
        }, ct),
        SensationRegistryChangedEvent r => _recorder.RecordLifecycleAsync(new LifecycleRecord
        {
            Timestamp = r.Timestamp,
            BackendId = r.BackendId,
            EventKind = r.Change == SensationRegistryChange.Registered
                ? "SensationRegistered"
                : "SensationUnregistered",
            SensationName = r.SensationName,
        }, ct),
        _ => Task.CompletedTask,
    };
}

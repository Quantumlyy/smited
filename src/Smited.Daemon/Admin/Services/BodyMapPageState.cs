using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Events;

namespace Smited.Daemon.Admin.Services;

/// <summary>
/// Per-zone activity snapshot held by <see cref="BodyMapPageState"/>. The
/// renderer reads <see cref="LastFiredAt"/> at render time and computes
/// the fade against the current clock, so the visual decay is smooth
/// even when no events arrive between render passes.
/// </summary>
internal sealed record ZoneActivity(
    bool IsActive,
    DateTimeOffset LastFiredAt,
    uint LastIntensity);

/// <summary>
/// Server-side state for the admin body map page. Subscribes to the
/// daemon's <see cref="EventBus"/> on host startup and maintains a
/// per-(backend, zone) activity map for the rendered silhouette.
/// </summary>
/// <remarks>
/// Registered as both a singleton and a hosted service in
/// <c>AdminConfiguration</c>: the singleton registration lets Blazor
/// components inject and read state; the hosted-service alias drives
/// <see cref="StartAsync"/>/<see cref="StopAsync"/> through the host
/// lifecycle.
///
/// Multiple connected admin clients share one instance — there is no
/// per-circuit state, just an event-driven snapshot every component
/// can read.
///
/// Forbidden-zone detection on the rendering side relies on injecting
/// the concrete <c>BodyMapState</c> (already DI-registered alongside
/// <c>IBodyMapState</c> at <c>Program.cs</c>) to reach
/// <c>ZoneRegions</c>. Don't collapse that dual registration without
/// a corresponding accessor on the interface.
/// </remarks>
internal sealed class BodyMapPageState : IHostedService
{
    private readonly EventBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<BodyMapPageState> _log;
    private readonly object _lock = new();
    private readonly Dictionary<(string BackendId, string ZoneId), ZoneActivity> _zones = new();

    private EventBus.Subscription? _subscription;
    private CancellationTokenSource? _cts;
    private Task? _consumer;

    public BodyMapPageState(EventBus bus, TimeProvider time, ILogger<BodyMapPageState> log)
    {
        _bus = bus;
        _time = time;
        _log = log;
    }

    /// <summary>Raised after every state mutation. Blazor components
    /// marshal a re-render via <c>InvokeAsync(StateHasChanged)</c>.</summary>
    public event Action? StateChanged;

    /// <summary>Returns a copy of the per-zone activity map at call
    /// time. Renderer combines this with the current clock to compute
    /// fade.</summary>
    public IReadOnlyDictionary<(string BackendId, string ZoneId), ZoneActivity> Snapshot()
    {
        lock (_lock)
        {
            return new Dictionary<(string, string), ZoneActivity>(_zones);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _bus.Subscribe(1024, BoundedChannelFullMode.DropOldest);
        _cts = new CancellationTokenSource();
        _consumer = Task.Run(() => ConsumeAsync(_subscription.Reader, _cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            try { await _cts.CancelAsync(); }
            catch (ObjectDisposedException) { }
        }

        if (_subscription is not null)
        {
            try { await _subscription.DisposeAsync(); }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "BodyMapPageState subscription disposal failed during shutdown");
            }
            _subscription = null;
        }

        if (_consumer is not null)
        {
            try { await _consumer.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task ConsumeAsync(ChannelReader<BackendEvent> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var evt in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                HandleEvent(evt);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "BodyMapPageState consumer crashed");
        }
    }

    private void HandleEvent(BackendEvent evt)
    {
        bool changed = false;

        switch (evt)
        {
            case SensationStarted started:
                if (started.ZoneIds.Count == 0) break;
                var now = _time.GetUtcNow();
                var intensity = started.IntensityPercent ?? 50u;
                lock (_lock)
                {
                    foreach (var zoneId in started.ZoneIds)
                    {
                        _zones[(started.BackendId, zoneId)] = new ZoneActivity(
                            IsActive: true,
                            LastFiredAt: now,
                            LastIntensity: intensity);
                    }
                }
                changed = true;
                break;

            case SensationCompleted completed:
                changed = ClearActive(completed.BackendId);
                break;

            case SensationCancelled cancelled:
                changed = ClearActive(cancelled.BackendId);
                break;
        }

        if (changed) StateChanged?.Invoke();
    }

    private bool ClearActive(string backendId)
    {
        bool any = false;
        lock (_lock)
        {
            foreach (var key in _zones.Keys.Where(k => k.BackendId == backendId).ToArray())
            {
                var prev = _zones[key];
                if (prev.IsActive)
                {
                    _zones[key] = prev with { IsActive = false };
                    any = true;
                }
            }
        }
        return any;
    }
}

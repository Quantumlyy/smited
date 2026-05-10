using System.Collections.Concurrent;
using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Smited.Daemon.Backends.Internal;
using Smited.V1;
using ParameterValue = Smited.Daemon.Backends.Internal.ParameterValue;

namespace Smited.Daemon.Backends.Mock;

/// <summary>
/// Faithful in-process simulation of a bHaptics TactSuit X40. Mirrors the
/// real backend's zone topology, parameter schema and concurrency model
/// closely enough that the gRPC surface, event streaming and concurrency
/// behaviour can be exercised end-to-end on Mac without hardware or the
/// bHaptics Player application. Timing is deterministic given a
/// <see cref="TimeProvider"/> — tests inject <c>FakeTimeProvider</c> to
/// fast-forward.
///
/// Differs from <see cref="MockOwoBackend"/> on every interesting axis:
/// 40 vibration motors vs 10 TENS zones, no calibration, motor-summing
/// PRIORITY concurrency vs exclusive CANCEL_OLDEST. Exercising the
/// abstraction with a backend this different validates that
/// <see cref="IHapticBackend"/> can carry hardware whose model isn't
/// shaped like OWO's.
/// </summary>
public sealed class MockBhapticsBackend : IHapticBackend, IMockBhapticsController
{
    private readonly TimeProvider _time;
    private readonly ILogger<MockBhapticsBackend> _logger;
    private readonly Channel<BackendEvent> _events = Channel.CreateUnbounded<BackendEvent>();
    private readonly ConcurrentDictionary<string, ActivePlayback> _playbacks =
        new(StringComparer.OrdinalIgnoreCase);

    private ZoneTopology _zones;
    private BackendStatus _status = BackendStatus.Ready;

    public MockBhapticsBackend(TimeProvider time, ILogger<MockBhapticsBackend> logger)
    {
        _time = time;
        _logger = logger;
        _zones = BhapticsTopology.BuildZones(accessoriesPresent: false);
        Parameters = BhapticsTopology.BuildParameters();
        Concurrency = new ConcurrencyModel
        {
            // bHaptics' real hardware permits unlimited concurrent
            // sensations (motors sum) but a sanity cap prevents runaway
            // haptic stacking. PRIORITY matches the contract that
            // higher-priority triggers preempt lower-priority ones;
            // equal-priority within capacity stack normally.
            MaxConcurrent = 4,
            Policy = ConcurrencyPolicy.Priority,
        };
    }

    public string Id => "mock-bhaptics";

    public string Kind => "bhaptics_tactsuit";

    public string DisplayName => "Mock TactSuit X40";

    public BackendStatus Status => _status;

    public IReadOnlyList<string> Capabilities { get; } = new[]
    {
        "vibration",
        "zoned",
        "wireless",
        "configurable_intensity",
        "concurrent_sensations",
        "sensation_registry_mutable",
    };

    public ZoneTopology Zones => _zones;

    public ParameterSchema Parameters { get; }

    public ConcurrencyModel Concurrency { get; }

    /// <summary>
    /// Always <c>null</c>: bHaptics has no per-user calibration flow
    /// comparable to OWO's. Intensity is tuned via the Player app's
    /// global slider, not stored per-user.
    /// </summary>
    public CalibrationState? Calibration => null;

    public Struct? Extras => null;

    public IAsyncEnumerable<BackendEvent> Events => _events.Reader.ReadAllAsync();

    public IReadOnlyCollection<string> ActiveSensationIds => _playbacks.Keys.ToArray();

    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<BackendTriggerResult> TriggerAsync(BackendTriggerRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var estimated = ComputeEstimatedDuration(request);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var playback = new ActivePlayback(request.SensationId, linked);
        _playbacks[request.SensationId] = playback;

        EmitEvent(new SensationStarted(
            Id,
            _time.GetUtcNow(),
            request.SensationId,
            request.SensationName,
            request.ClientTraceId));

        _logger.LogInformation(
            "Mock bHaptics firing {SensationId} ({SensationName}) on {Zones} for {Duration}",
            request.SensationId,
            request.SensationName ?? "<inline>",
            string.Join(",", request.ZoneIds),
            estimated);

        // Create the Task.Delay synchronously here so its timer is
        // registered with `_time` before TriggerAsync returns. Otherwise
        // a test that calls Time.Advance immediately after Trigger races
        // the Task.Run scheduling and the delay may fire on a later
        // advance — or not at all in the same test.
        var delay = estimated > TimeSpan.Zero
            ? Task.Delay(estimated, _time, linked.Token)
            : Task.CompletedTask;

        _ = Task.Run(async () =>
        {
            BackendEvent finalEvent;
            try
            {
                await delay.ConfigureAwait(false);
                finalEvent = new SensationCompleted(
                    Id,
                    _time.GetUtcNow(),
                    request.SensationId,
                    request.SensationName,
                    request.ClientTraceId);
            }
            catch (OperationCanceledException)
            {
                finalEvent = new SensationCancelled(
                    Id,
                    _time.GetUtcNow(),
                    request.SensationId,
                    request.SensationName,
                    request.ClientTraceId,
                    Reason: "preempted_or_stopped");
            }
            finally
            {
                _playbacks.TryRemove(request.SensationId, out _);
                linked.Dispose();
            }
            EmitEvent(finalEvent);
        });

        return Task.FromResult(new BackendTriggerResult(request.SensationId, estimated));
    }

    public Task<int> StopAsync(BackendStopRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopped = 0;
        if (request.All)
        {
            foreach (var (id, _) in _playbacks)
            {
                if (_playbacks.TryRemove(id, out var removed))
                {
                    SafeCancel(removed.Cts);
                    stopped++;
                }
            }
        }
        else if (!string.IsNullOrEmpty(request.SensationId) &&
                 _playbacks.TryRemove(request.SensationId, out var p))
        {
            SafeCancel(p.Cts);
            stopped++;
        }
        return Task.FromResult(stopped);
    }

    public void SetAccessoriesPresent(bool present)
    {
        _zones = BhapticsTopology.BuildZones(accessoriesPresent: present);
        EmitEvent(new BackendLifecycleEvent(
            Id,
            _time.GetUtcNow(),
            BackendLifecycleChange.StatusChanged,
            BackendSummarySnapshot.Of(this),
            Reason: present ? "accessories_present" : "accessories_absent"));
    }

    public void EmitStatusChange(BackendStatus newStatus, string? reason = null)
    {
        _status = newStatus;
        EmitEvent(new BackendLifecycleEvent(
            Id,
            _time.GetUtcNow(),
            BackendLifecycleChange.StatusChanged,
            BackendSummarySnapshot.Of(this),
            reason));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var p in _playbacks.Values)
        {
            SafeCancel(p.Cts);
        }
        _playbacks.Clear();
        _events.Writer.TryComplete();
        await Task.CompletedTask;
    }

    private void EmitEvent(BackendEvent evt) => _events.Writer.TryWrite(evt);

    private static void SafeCancel(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException) { }
    }

    private static TimeSpan ComputeEstimatedDuration(BackendTriggerRequest request)
    {
        // Microsensations play sequentially, summing per-step durations.
        // Same rationale as the OWO mock: taking the max would let
        // multi-pulse sensations release the concurrency slot too early.
        var total = TimeSpan.Zero;
        foreach (var micro in request.Microsensations)
        {
            total += ReadDuration(micro, "duration");
        }
        return total;
    }

    private static TimeSpan ReadDuration(MicrosensationParameters micro, string key) =>
        micro.Values.TryGetValue(key, out var v) && v is ParameterValue.Duration d ? d.Value : TimeSpan.Zero;

    private sealed class ActivePlayback
    {
        public ActivePlayback(string sensationId, CancellationTokenSource cts)
        {
            SensationId = sensationId;
            Cts = cts;
        }

        public string SensationId { get; }

        public CancellationTokenSource Cts { get; }
    }
}

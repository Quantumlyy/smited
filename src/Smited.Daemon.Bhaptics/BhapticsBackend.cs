using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Smited.Daemon.Backends;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Bhaptics.WebSocket;
using Smited.Daemon.BodyMap;
using Smited.V1;
using ParameterValue = Smited.Daemon.Backends.Internal.ParameterValue;
using ProtoStruct = Google.Protobuf.WellKnownTypes.Struct;
using ProtoValue = Google.Protobuf.WellKnownTypes.Value;

namespace Smited.Daemon.Bhaptics;

/// <summary>
/// Real bHaptics backend: speaks the WebSocket v2 protocol to a locally
/// running bHaptics Player. Translates smited's domain model (zones,
/// sensations, microsensations) to bHaptics' wire format (positions,
/// dot patterns, intensity arrays) and surfaces Player-reported device
/// status as <c>Extras</c>.
///
/// Loaded reflectively by the daemon's BackendBootstrapper when
/// <c>Smited:Backends:EnableBhaptics</c> is true and the host is
/// running on Windows. The class itself is platform-agnostic at
/// build time (only depends on BCL <c>System.Net.WebSockets</c>);
/// the Windows-only constraint is at runtime because the Player
/// itself only runs on Windows.
/// </summary>
public sealed class BhapticsBackend : IHapticBackend
{
    private readonly BhapticsBackendOptions _options;
    private readonly ILogger<BhapticsBackend> _log;
    private readonly TimeProvider _time;
    private readonly Channel<BackendEvent> _events = Channel.CreateUnbounded<BackendEvent>();
    private readonly ConcurrentDictionary<string, ActivePlayback> _playbacks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ParameterSchema _parameters;
    private readonly ConcurrencyModel _concurrency;

    private ZoneTopology _zones;
    private PlayerClient? _client;
    private BackendStatus _status = BackendStatus.Disconnected;
    private ProtoStruct? _extras;
    private bool _accessoriesAdvertised;
    private TaskCompletionSource<bool>? _initialStatusReceived;
    private CancellationTokenSource? _reconnectCts;
    private volatile bool _disposed;
    private string _displayName = "bHaptics TactSuit";

    public BhapticsBackend(
        BhapticsBackendOptions options,
        ILogger<BhapticsBackend> log,
        TimeProvider time)
    {
        _options = options;
        _log = log;
        _time = time;
        _zones = BhapticsTopology.BuildZones(accessoriesPresent: false);
        _parameters = BhapticsTopology.BuildParameters();
        _concurrency = new ConcurrencyModel
        {
            MaxConcurrent = 4,
            Policy = ConcurrencyPolicy.Priority,
        };
    }

    public string Id => _options.BackendId;

    public string Kind => "bhaptics_tactsuit";

    public string DisplayName => _displayName;

    public BackendStatus Status => _status;

    /// <summary>
    /// bHaptics motors are vibration-only; the manufacturer publishes
    /// no forbidden regions. The smited-default forbidden regions
    /// (e.g. ChestOverHeart) still apply, layered on top by the
    /// bodymap validator at registration time.
    /// </summary>
    public IReadOnlySet<BodyRegion> ForbiddenRegions { get; } = ImmutableHashSet<BodyRegion>.Empty;

    /// <summary>
    /// Replaces the default <see cref="DisplayName"/> with a per-descriptor
    /// override applied by the factory before <c>ConnectAsync</c>. Public
    /// so the factory in the same assembly can call it without touching
    /// internal state directly through a sibling namespace.
    /// </summary>
    public void OverrideDisplayName(string displayName)
    {
        ArgumentException.ThrowIfNullOrEmpty(displayName);
        _displayName = displayName;
    }

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

    public ParameterSchema Parameters => _parameters;

    public ConcurrencyModel Concurrency => _concurrency;

    /// <summary>
    /// Always <c>null</c>: bHaptics has no per-user calibration flow.
    /// Intensity is tuned via the Player app's global slider, not
    /// stored per-user.
    /// </summary>
    public CalibrationState? Calibration => null;

    public ProtoStruct? Extras => _extras;

    public IAsyncEnumerable<BackendEvent> Events => _events.Reader.ReadAllAsync();

    public async Task ConnectAsync(CancellationToken ct)
    {
        await OpenConnectionAsync(ct).ConfigureAwait(false);

        // Wait briefly for the Player to push its first deviceStatus
        // frame so any paired accessories are reflected in the topology
        // before SensationLoader validates persisted sensations against
        // it. Without this, an arm_l_*-targeting sensation persisted
        // from a previous run could fail boot validation against the
        // vest-only initial topology — even when the user's hardware
        // is actually attached. The wait is bounded:
        // InitialStatusTimeoutMillis defaults to 1500ms, more than
        // enough for a healthy local Player but short enough that a
        // missing or slow Player doesn't block daemon startup forever.
        var initialStatus = _initialStatusReceived;
        var timeout = TimeSpan.FromMilliseconds(_options.InitialStatusTimeoutMillis);
        if (timeout > TimeSpan.Zero && initialStatus is not null)
        {
            var winner = await Task.WhenAny(initialStatus.Task, Task.Delay(timeout, ct)).ConfigureAwait(false);
            if (winner != initialStatus.Task)
            {
                _log.LogDebug(
                    "bHaptics Player did not push deviceStatus within {Timeout}; proceeding with vest-only topology",
                    timeout);
            }
        }
    }

    private async Task OpenConnectionAsync(CancellationToken ct)
    {
        // Tear down any prior client (the reconnect path calls this on
        // a stale instance whose read loop has already terminated).
        // Clear the field BEFORE disposing so that if the new
        // connection attempt fails, BhapticsBackend.DisposeAsync
        // doesn't try to dispose the same client a second time.
        var existing = _client;
        _client = null;
        if (existing is not null)
        {
            existing.DeviceStatusChanged -= OnDeviceStatusChanged;
            existing.Disconnected -= OnDisconnected;
            await existing.DisposeAsync().ConfigureAwait(false);
        }

        _initialStatusReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var endpoint = new Uri(_options.PlayerEndpoint);
        var client = new PlayerClient(endpoint, _log);
        client.DeviceStatusChanged += OnDeviceStatusChanged;
        client.Disconnected += OnDisconnected;

        await client.ConnectAsync(ct).ConfigureAwait(false);
        _client = client;
        _status = BackendStatus.Ready;
    }

    public Task<BackendTriggerResult> TriggerAsync(BackendTriggerRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Reject synchronously when the Player isn't connected. Without
        // this check, _client wraps a closed socket after OnDisconnected
        // and the trigger gets accepted=true to the caller, then fails
        // asynchronously inside SendAsync — surfacing as a stray
        // SensationCancelled with reason "playback_error". The
        // coordinator catches this exception and returns accepted=false
        // with the message, which is the right shape for the gRPC
        // client.
        if (_status != BackendStatus.Ready || _client is null)
        {
            throw new InvalidOperationException(
                $"BhapticsBackend cannot trigger: status is {_status}. Is bHaptics Player running and connected?");
        }
        var client = _client;

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

        _ = Task.Run(async () =>
        {
            BackendEvent finalEvent;
            try
            {
                await PlayAsync(client, request, playback, linked.Token).ConfigureAwait(false);
                finalEvent = new SensationCompleted(
                    Id,
                    _time.GetUtcNow(),
                    request.SensationId,
                    request.SensationName,
                    request.ClientTraceId);
            }
            catch (OperationCanceledException)
            {
                await CancelPlayerKeysAsync(playback).ConfigureAwait(false);
                finalEvent = new SensationCancelled(
                    Id,
                    _time.GetUtcNow(),
                    request.SensationId,
                    request.SensationName,
                    request.ClientTraceId,
                    Reason: "preempted_or_stopped");
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "BhapticsBackend trigger {SensationId} failed mid-playback",
                    request.SensationId);
                finalEvent = new SensationCancelled(
                    Id,
                    _time.GetUtcNow(),
                    request.SensationId,
                    request.SensationName,
                    request.ClientTraceId,
                    Reason: "playback_error");
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

    private async Task PlayAsync(
        PlayerClient client,
        BackendTriggerRequest request,
        ActivePlayback playback,
        CancellationToken ct)
    {
        // The coordinator validates that every entry in request.ZoneIds
        // is either a known motor zone or a known group; expand groups
        // here so we resolve only motor IDs through ZoneIndexMap. The
        // sample sensations under sensations/bhaptics_tactsuit/ all
        // target groups (front_chest, torso, back_shoulders), which is
        // why this expansion has to happen before the dot pattern is
        // built.
        var motorZones = BhapticsTopology.ExpandGroupZoneIds(_zones, request.ZoneIds);

        // Group motor zones by their resolved Position. A "torso"
        // trigger spans both halves of the vest, which has to ship as
        // two frames (one VestFront, one VestBack) rather than one
        // Position.Vest frame — motor index 5 means a different motor
        // on each half, so a single Vest frame would collide.
        // Accessory zones (gloves/sleeves) get their own per-Position
        // frames the same way.
        var positionGroups = motorZones
            .Select(ZoneIndexMap.Resolve)
            .GroupBy(t => t.position, t => t.motorIndex)
            .ToArray();

        foreach (var micro in request.Microsensations)
        {
            ct.ThrowIfCancellationRequested();

            var intensity = ReadIntensity(micro, request.IntensityScale);
            var duration = ReadDuration(micro, "duration");
            if (duration <= TimeSpan.Zero) continue;

            foreach (var group in positionGroups)
            {
                var dots = group.Select(idx => new DotPoint(idx, intensity)).ToArray();
                var key = await client.SubmitDotPatternAsync(group.Key, dots, duration, ct).ConfigureAwait(false);
                playback.AddPatternKey(key);
            }

            await Task.Delay(duration, _time, ct).ConfigureAwait(false);
        }
    }

    public async Task<int> StopAsync(BackendStopRequest request, CancellationToken ct)
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
            if (_client is not null)
            {
                await _client.CancelAllAsync(ct).ConfigureAwait(false);
            }
        }
        else if (!string.IsNullOrEmpty(request.SensationId) &&
                 _playbacks.TryRemove(request.SensationId, out var p))
        {
            SafeCancel(p.Cts);
            await CancelPlayerKeysAsync(p).ConfigureAwait(false);
            stopped++;
        }
        return stopped;
    }

    private async Task CancelPlayerKeysAsync(ActivePlayback playback)
    {
        if (_client is null) return;
        foreach (var key in playback.SnapshotPatternKeys())
        {
            await _client.TryCancelAsync(key, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void OnDeviceStatusChanged(IReadOnlyList<DeviceStatus> devices)
    {
        _extras = BuildExtras(devices);

        // The first deviceStatus arriving after a fresh connection
        // unblocks ConnectAsync's bounded wait so callers see the
        // populated accessory set before Resume.
        _initialStatusReceived?.TrySetResult(true);

        // bHaptics Player pushes a deviceStatus frame whenever paired
        // hardware changes (and as a periodic heartbeat). Reconcile our
        // advertised topology with what the Player reports: if any
        // TactSleeve / TactGlove is connected, expand to include their
        // motor zones; otherwise revert to vest-only. Re-emit a
        // BackendLifecycleEvent only when the advertised set actually
        // changes — heartbeat frames with the same membership are
        // ignored.
        var hasAccessory = devices.Any(d => d.Connected && IsAccessoryPosition(d.Position));
        if (hasAccessory == _accessoriesAdvertised) return;

        _accessoriesAdvertised = hasAccessory;
        _zones = BhapticsTopology.BuildZones(accessoriesPresent: hasAccessory);
        EmitEvent(new BackendLifecycleEvent(
            Id,
            _time.GetUtcNow(),
            BackendLifecycleChange.StatusChanged,
            BackendSummarySnapshot.Of(this),
            Reason: hasAccessory ? "accessories_present" : "accessories_absent"));
    }

    private static bool IsAccessoryPosition(Position position) =>
        position is Position.ForearmL or Position.ForearmR or Position.GloveL or Position.GloveR;

    private void OnDisconnected(Exception? terminal)
    {
        if (_disposed) return;

        // Cancel every in-flight playback. Without this, a single-
        // microsensation sensation in its Task.Delay would just wait
        // out the duration on a closed socket and emit
        // SensationCompleted as if it had played, even though no
        // frame actually reached the suit. Cancelling the linked CTS
        // routes the playback through the OperationCanceledException
        // branch, which emits SensationCancelled with reason
        // "preempted_or_stopped".
        foreach (var p in _playbacks.Values)
        {
            SafeCancel(p.Cts);
        }

        _status = BackendStatus.Disconnected;
        EmitEvent(new BackendLifecycleEvent(
            Id,
            _time.GetUtcNow(),
            BackendLifecycleChange.StatusChanged,
            BackendSummarySnapshot.Of(this),
            Reason: terminal?.GetType().Name ?? "peer_close"));

        if (_options.MaxReconnectAttempts <= 0)
        {
            _status = BackendStatus.Error;
            EmitEvent(new BackendLifecycleEvent(
                Id,
                _time.GetUtcNow(),
                BackendLifecycleChange.StatusChanged,
                BackendSummarySnapshot.Of(this),
                Reason: "reconnect_disabled"));
            return;
        }

        // Cancel any prior reconnect loop and start a fresh one. Two
        // OnDisconnected calls in flight (e.g. flap during reconnect)
        // collapse to one active loop via the atomic swap.
        var newCts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _reconnectCts, newCts);
        oldCts?.Cancel();
        oldCts?.Dispose();
        _ = Task.Run(() => ReconnectLoopAsync(newCts.Token));
    }

    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= _options.MaxReconnectAttempts; attempt++)
        {
            // Exponential backoff: 1s, 2s, 4s, ...  Capped implicitly
            // by MaxReconnectAttempts; a 4-attempt run waits at most
            // 1+2+4+8 = 15s before giving up.
            var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
            try
            {
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await OpenConnectionAsync(ct).ConfigureAwait(false);
                _log.LogInformation(
                    "Reconnected to bHaptics Player on attempt {Attempt}/{Max}",
                    attempt, _options.MaxReconnectAttempts);
                EmitEvent(new BackendLifecycleEvent(
                    Id,
                    _time.GetUtcNow(),
                    BackendLifecycleChange.StatusChanged,
                    BackendSummarySnapshot.Of(this),
                    Reason: "reconnected"));
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Reconnect attempt {Attempt}/{Max} to bHaptics Player at {Endpoint} failed",
                    attempt, _options.MaxReconnectAttempts, _options.PlayerEndpoint);
            }
        }

        if (ct.IsCancellationRequested) return;
        _status = BackendStatus.Error;
        EmitEvent(new BackendLifecycleEvent(
            Id,
            _time.GetUtcNow(),
            BackendLifecycleChange.StatusChanged,
            BackendSummarySnapshot.Of(this),
            Reason: "reconnect_exhausted"));
    }

    private static ProtoStruct BuildExtras(IReadOnlyList<DeviceStatus> devices)
    {
        var extras = new ProtoStruct();
        var deviceList = new Google.Protobuf.WellKnownTypes.ListValue();
        foreach (var d in devices)
        {
            var entry = new ProtoStruct();
            entry.Fields["position"] = ProtoValue.ForString(d.Position.ToString());
            entry.Fields["connected"] = ProtoValue.ForBool(d.Connected);
            entry.Fields["batteryPercent"] = ProtoValue.ForNumber(d.BatteryPercent);
            deviceList.Values.Add(ProtoValue.ForStruct(entry));
        }
        extras.Fields["devices"] = ProtoValue.ForList(deviceList.Values.ToArray());
        return extras;
    }

    public IReadOnlyCollection<string> ActiveSensationIds => _playbacks.Keys.ToArray();

    public async ValueTask DisposeAsync()
    {
        _disposed = true;

        var reconnectCts = Interlocked.Exchange(ref _reconnectCts, null);
        reconnectCts?.Cancel();
        reconnectCts?.Dispose();

        foreach (var p in _playbacks.Values)
        {
            SafeCancel(p.Cts);
        }
        _playbacks.Clear();
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
        _events.Writer.TryComplete();
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
        var total = TimeSpan.Zero;
        foreach (var micro in request.Microsensations)
        {
            total += ReadDuration(micro, "duration");
        }
        return total;
    }

    private static TimeSpan ReadDuration(MicrosensationParameters micro, string key) =>
        micro.Values.TryGetValue(key, out var v) && v is ParameterValue.Duration d ? d.Value : TimeSpan.Zero;

    private static int ReadIntensity(MicrosensationParameters micro, uint? intensityScale)
    {
        var raw = micro.Values.TryGetValue("intensity", out var v) && v is ParameterValue.Number n
            ? (int)n.Value
            : 0;
        if (intensityScale is { } scale)
        {
            raw = (int)Math.Round(raw * (scale / 100.0));
        }
        return Math.Clamp(raw, 0, 100);
    }

    private sealed class ActivePlayback
    {
        private readonly List<string> _patternKeys = new();

        public ActivePlayback(string sensationId, CancellationTokenSource cts)
        {
            SensationId = sensationId;
            Cts = cts;
        }

        public string SensationId { get; }

        public CancellationTokenSource Cts { get; }

        public void AddPatternKey(string key)
        {
            lock (_patternKeys) _patternKeys.Add(key);
        }

        public IReadOnlyList<string> SnapshotPatternKeys()
        {
            lock (_patternKeys) return _patternKeys.ToArray();
        }
    }
}

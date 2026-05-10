using System.Collections.Concurrent;
using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Smited.Daemon.Backends;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Bhaptics.WebSocket;
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

    public string DisplayName => "bHaptics TactSuit";

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
        var endpoint = new Uri(_options.PlayerEndpoint);
        _client = new PlayerClient(endpoint, _log);
        _client.DeviceStatusChanged += OnDeviceStatusChanged;
        _client.Disconnected += OnDisconnected;

        await _client.ConnectAsync(ct).ConfigureAwait(false);
        _status = BackendStatus.Ready;
    }

    public Task<BackendTriggerResult> TriggerAsync(BackendTriggerRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var client = _client ?? throw new InvalidOperationException("BhapticsBackend is not connected.");

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
        var position = ZoneIndexMap.EnclosingPosition(request.ZoneIds);
        var motorIndices = request.ZoneIds.Select(ZoneIndexMap.Resolve).Select(t => t.motorIndex).ToArray();

        foreach (var micro in request.Microsensations)
        {
            ct.ThrowIfCancellationRequested();

            var intensity = ReadIntensity(micro, request.IntensityScale);
            var duration = ReadDuration(micro, "duration");
            if (duration <= TimeSpan.Zero) continue;

            var dots = motorIndices.Select(idx => new DotPoint(idx, intensity)).ToArray();
            var key = await client.SubmitDotPatternAsync(position, dots, duration, ct).ConfigureAwait(false);
            playback.AddPatternKey(key);

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
        _status = BackendStatus.Disconnected;
        EmitEvent(new BackendLifecycleEvent(
            Id,
            _time.GetUtcNow(),
            BackendLifecycleChange.StatusChanged,
            BackendSummarySnapshot.Of(this),
            Reason: terminal?.GetType().Name ?? "peer_close"));
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

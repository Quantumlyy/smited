// This file is excluded from compile on non-Windows hosts via the
// conditional <Compile Remove="OwoBackend.cs"/> ItemGroup in
// Smited.Daemon.Owo.csproj. The OWO SDK is restored only on Windows
// (the WINDOWS symbol is automatically defined by the net9.0-windows
// TFM, but on cross-platform builds the SDK package isn't present, so
// we additionally guard the file body with `#if WINDOWS`).

#if WINDOWS
using System.Collections.Concurrent;
using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Smited.Daemon.Backends;
using Smited.Daemon.Backends.Internal;
using Smited.V1;
using ParameterValue = Smited.Daemon.Backends.Internal.ParameterValue;

namespace Smited.Daemon.Owo;

/// <summary>
/// Real OWO Skin haptic backend, backed by the official OWO C# SDK
/// (NuGet package <c>OWO</c>) and the locally-running MyOWO desktop app.
/// </summary>
/// <remarks>
/// <para>
/// Connects via the SDK's auto-discovery handshake (or with a manual IP
/// from <see cref="OwoBackendOptions.ManualIp"/>) to a paired, calibrated
/// OWO Skin reachable through the MyOWO app's local TCP service.
/// Sensations are translated from smited's domain model (zones,
/// microsensations, parameters) into the SDK's <c>SensationsFactory</c>
/// API.
/// </para>
/// <para>
/// Single-shot per OWO's own concurrency rules: only one sensation plays
/// at a time, and a new <c>Send</c> cancels the previous. The reported
/// <see cref="Concurrency"/> matches that reality
/// (<c>max_concurrent: 1</c>, <c>policy: CANCEL_OLDEST</c>) and is
/// intentionally identical to <c>MockOwoBackend</c>'s, so a sensation
/// library authored against the mock works against the real backend
/// without modification.
/// </para>
/// </remarks>
public sealed class OwoBackend : IHapticBackend
{
    private readonly OwoBackendOptions _options;
    private readonly IOwoSdk _sdk;
    private readonly TimeProvider _time;
    private readonly ILogger<OwoBackend> _logger;
    private readonly Channel<BackendEvent> _events = Channel.CreateBounded<BackendEvent>(
        new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly ConcurrentDictionary<string, ActivePlayback> _activeSensations =
        new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _lifetimeCts;
    private Task? _heartbeatTask;
    private bool _lastSeenConnected;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _zoneGroupMembers;

    /// <summary>
    /// Constructed by the daemon's <c>BackendBootstrapper</c> via
    /// <c>ActivatorUtilities.CreateInstance</c>. All collaborators are
    /// resolved from the host DI container — <see cref="IOwoSdk"/> is
    /// registered to <c>StaticOwoSdk</c> on Windows when
    /// <c>EnableOwo</c> is true, otherwise this backend never gets
    /// constructed.
    /// </summary>
    public OwoBackend(
        OwoBackendOptions options,
        IOwoSdk sdk,
        TimeProvider time,
        ILogger<OwoBackend> logger)
    {
        _options = options;
        _sdk = sdk;
        _time = time;
        _logger = logger;

        Zones = BuildZones();
        Parameters = BuildParameters();
        Concurrency = new ConcurrencyModel
        {
            MaxConcurrent = 1,
            Policy = ConcurrencyPolicy.CancelOldest,
        };

        // Memoize group membership so trigger-time expansion is just a
        // dictionary lookup. The validator upstream accepts any zone id
        // advertised in Zones (leaves AND groups), but the OWO SDK only
        // knows about leaves — we have to expand groups here before
        // OwoMuscleMap.Resolve runs.
        _zoneGroupMembers = Zones.Groups.ToDictionary(
            g => g.Id,
            g => (IReadOnlyList<string>)g.ZoneIds.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public string Id => _options.BackendId;

    /// <inheritdoc />
    public string Kind => "owo_skin";

    /// <inheritdoc />
    public string DisplayName => "OWO Skin";

    /// <inheritdoc />
    public BackendStatus Status { get; private set; } = BackendStatus.Disconnected;

    /// <inheritdoc />
    public IReadOnlyList<string> Capabilities { get; } = new[]
    {
        "ems", "zoned", "calibrated",
    };

    /// <inheritdoc />
    public ZoneTopology Zones { get; }

    /// <inheritdoc />
    public ParameterSchema Parameters { get; }

    /// <inheritdoc />
    public ConcurrencyModel Concurrency { get; }

    /// <summary>
    /// Calibration mirror. <c>null</c> until <see cref="ConnectAsync"/>
    /// succeeds, after which it reads as <c>Calibrated = true</c> with a
    /// connect-time stamp — see the constructor remarks on
    /// <c>LastCalibratedAt</c> for why the timestamp is approximate.
    /// </summary>
    public CalibrationState? Calibration { get; private set; }

    /// <inheritdoc />
    public Struct? Extras => null;

    /// <inheritdoc />
    public IAsyncEnumerable<BackendEvent> Events => _events.Reader.ReadAllAsync();

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken ct)
    {
        Status = BackendStatus.Disconnected;

        _sdk.Configure(_options.GameDisplayName);

        try
        {
            if (!string.IsNullOrEmpty(_options.ManualIp))
            {
                _logger.LogInformation(
                    "OWO backend {Id} connecting to MyOWO at {Ip}",
                    Id, _options.ManualIp);
                await _sdk.ConnectAsync(_options.ManualIp).WaitAsync(ct).ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation(
                    "OWO backend {Id} auto-connecting to MyOWO; pick this entry in the MyOWO 'Scan Games' panel if pairing stalls",
                    Id);
                await _sdk.AutoConnectAsync().WaitAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            Status = BackendStatus.Disconnected;
            throw;
        }
        catch (Exception ex)
        {
            Status = BackendStatus.Error;
            _logger.LogError(ex,
                "OWO backend {Id} failed to connect; ensure MyOWO is running and the device is paired and calibrated",
                Id);
            throw;
        }

        if (!_sdk.IsConnected)
        {
            Status = BackendStatus.Error;
            throw new InvalidOperationException(
                "OWO SDK reports IsConnected=false after Connect/AutoConnect succeeded");
        }

        Status = BackendStatus.Ready;
        _lastSeenConnected = true;
        // The MyOWO app refuses to pair with an uncalibrated device, so the
        // moment AutoConnect/Connect succeeds we know calibration is present.
        // The SDK does not expose the calibration timestamp from MyOWO, so we
        // record the connect time here as a best-effort approximation.
        Calibration = new CalibrationState
        {
            Calibrated = true,
            LastCalibratedAt = Timestamp.FromDateTimeOffset(_time.GetUtcNow()),
        };

        _logger.LogInformation(
            "OWO backend {Id} connected, calibrated and ready", Id);

        StartHeartbeat();
    }

    private void StartHeartbeat()
    {
        // Idempotent: a reconnect path may rerun ConnectAsync on a still-
        // running backend. Reuse the existing loop in that case.
        if (_heartbeatTask is { IsCompleted: false })
        {
            return;
        }

        _lifetimeCts = new CancellationTokenSource();
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_lifetimeCts.Token));
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        var period = TimeSpan.FromSeconds(Math.Max(1, _options.HeartbeatSeconds));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(period, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            bool connected;
            try
            {
                connected = _sdk.IsConnected;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "OWO backend {Id} heartbeat failed reading IsConnected", Id);
                continue;
            }

            if (connected == _lastSeenConnected)
            {
                continue;
            }

            _lastSeenConnected = connected;
            if (connected)
            {
                Status = BackendStatus.Ready;
                EmitLifecycleStatusChanged("reconnected");
                _logger.LogInformation("OWO backend {Id} transport restored", Id);
            }
            else
            {
                Status = BackendStatus.Disconnected;
                EmitLifecycleStatusChanged("transport dropped");
                _logger.LogWarning(
                    "OWO backend {Id} transport dropped; sensations will fail until reconnected",
                    Id);
                if (_options.MaxReconnectAttempts > 0)
                {
                    _ = TryReconnectAsync(ct);
                }
                // With MaxReconnectAttempts == 0 the heartbeat loop
                // continues to poll IsConnected; if the SDK reports a
                // restored connection on a later tick the Ready branch
                // above flips Status back. Without this guard we'd
                // immediately fall into the "exhausted" path of
                // TryReconnectAsync and stamp Status=Error after a
                // single tick, which contradicts "disable retries but
                // stay disconnected".
            }
        }
    }

    private async Task TryReconnectAsync(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= _options.MaxReconnectAttempts; attempt++)
        {
            try
            {
                var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                await Task.Delay(backoff, _time, ct).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(_options.ManualIp))
                {
                    await _sdk.ConnectAsync(_options.ManualIp).WaitAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    await _sdk.AutoConnectAsync().WaitAsync(ct).ConfigureAwait(false);
                }

                if (_sdk.IsConnected)
                {
                    _logger.LogInformation(
                        "OWO backend {Id} reconnected on attempt {Attempt}",
                        Id, attempt);
                    // The heartbeat tick observes the state flip and emits
                    // BackendLifecycleEvent itself; nothing to do here.
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "OWO backend {Id} reconnect attempt {Attempt} failed",
                    Id, attempt);
            }
        }

        Status = BackendStatus.Error;
        EmitLifecycleStatusChanged("reconnection exhausted");
        _logger.LogError(
            "OWO backend {Id} unable to reconnect after {Max} attempts; restart the daemon to retry",
            Id, _options.MaxReconnectAttempts);
    }

    private void EmitLifecycleStatusChanged(string reason) =>
        EmitEvent(new BackendLifecycleEvent(
            Id,
            _time.GetUtcNow(),
            BackendLifecycleChange.StatusChanged,
            BackendSummarySnapshot.Of(this),
            reason));

    /// <inheritdoc />
    public Task<BackendTriggerResult> TriggerAsync(
        BackendTriggerRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Status != BackendStatus.Ready)
        {
            throw new InvalidOperationException(
                $"OWO backend {Id} status is {Status}, cannot trigger");
        }

        var totalDuration = ComputeEstimatedDuration(request);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Stash an ActivePlayback (CTS + a TCS-backed completion signal)
        // so StopAsync can target a specific in-flight sensation, and so
        // DisposeAsync can wait for the SensationCancelled event to land
        // in the channel before tearing it down. OWO's exclusive
        // concurrency means there will only be at most one entry here at
        // a time given the Concurrency.Policy=CANCEL_OLDEST upstream
        // enforcement, but we still key by id for symmetry with the
        // mock backend.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playback = new ActivePlayback(linked, completion.Task);
        _activeSensations[request.SensationId] = playback;

        EmitEvent(new SensationStarted(
            Id,
            _time.GetUtcNow(),
            request.SensationId,
            request.SensationName,
            request.ClientTraceId));

        _logger.LogInformation(
            "OWO backend {Id} firing {SensationId} ({SensationName}) on {Zones} for {Duration}",
            Id,
            request.SensationId,
            request.SensationName ?? "<inline>",
            string.Join(",", request.ZoneIds),
            totalDuration);

        // Pre-register the total-duration timer synchronously so
        // FakeTimeProvider tests that advance time after TriggerAsync
        // returns observe a deterministic completion. The microsensation
        // dispatch loop runs on the thread pool and may register
        // additional inter-send timers; tests that need to drive
        // multi-microsensation playback step-by-step pump time more than
        // once.
        var totalDelay = totalDuration > TimeSpan.Zero
            ? Task.Delay(totalDuration, _time, linked.Token)
            : Task.CompletedTask;

        _ = Task.Run(async () =>
        {
            BackendEvent finalEvent;
            // anySent gates whether OperationCanceledException needs to
            // call _sdk.Stop(): if cancellation arrives mid-playback the
            // device is still vibrating, and only OWO.Stop() silences it.
            // If cancellation arrives before the first Send (e.g. caller
            // ct already cancelled), there's nothing on the device yet
            // and we skip the SDK stop entirely.
            var anySent = false;
            try
            {
                for (var i = 0; i < request.Microsensations.Count; i++)
                {
                    // Check cancellation BEFORE each Send so a token
                    // cancelled between iterations doesn't leak one more
                    // microsensation onto the device.
                    linked.Token.ThrowIfCancellationRequested();

                    var micro = request.Microsensations[i];
                    var command = BuildSendCommand(micro, request);
                    _sdk.Send(command);
                    anySent = true;

                    if (i < request.Microsensations.Count - 1)
                    {
                        var thisDuration = ResolveMicroDuration(micro);
                        if (thisDuration > TimeSpan.Zero)
                        {
                            await Task.Delay(thisDuration, _time, linked.Token).ConfigureAwait(false);
                        }
                    }
                }

                await totalDelay.ConfigureAwait(false);

                finalEvent = new SensationCompleted(
                    Id,
                    _time.GetUtcNow(),
                    request.SensationId,
                    request.SensationName,
                    request.ClientTraceId);
            }
            catch (OperationCanceledException)
            {
                // Only call _sdk.Stop() if we sent something AND nobody
                // else has already issued a Stop on our behalf. The
                // SkipSdkStop flag (set by StopAsync/DisposeAsync)
                // prevents CANCEL_OLDEST preemption from silencing the
                // replacement sensation: the coordinator's flow there is
                // StopAsync(old) — which calls _sdk.Stop() — followed by
                // TriggerAsync(new) — which calls _sdk.Send(). If we
                // also called Stop here, after the new Send had already
                // fired, we'd silence the device the user is now
                // expecting to feel.
                if (anySent && !playback.SkipSdkStop)
                {
                    try
                    {
                        _sdk.Stop();
                    }
                    catch (Exception stopEx)
                    {
                        _logger.LogDebug(stopEx,
                            "OWO backend {Id} threw on Stop() while cancelling {SensationId}",
                            Id, request.SensationId);
                    }
                }
                finalEvent = new SensationCancelled(
                    Id,
                    _time.GetUtcNow(),
                    request.SensationId,
                    request.SensationName,
                    request.ClientTraceId,
                    Reason: "stopped");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "OWO sensation {SensationId} failed mid-flight",
                    request.SensationId);
                if (anySent && !playback.SkipSdkStop)
                {
                    try { _sdk.Stop(); }
                    catch { /* swallow — already in an error path */ }
                }
                finalEvent = new SensationCancelled(
                    Id,
                    _time.GetUtcNow(),
                    request.SensationId,
                    request.SensationName,
                    request.ClientTraceId,
                    Reason: $"error: {ex.Message}");
            }
            finally
            {
                _activeSensations.TryRemove(request.SensationId, out _);
                linked.Dispose();
            }

            EmitEvent(finalEvent);
            // Resolve the completion signal AFTER the event lands in the
            // channel so DisposeAsync awaiting on this task observes the
            // SensationCancelled event before TryComplete shuts the
            // channel for further writes.
            completion.TrySetResult();
        });
        // Deliberately not passing `ct` to Task.Run: if the caller's
        // token is already cancelled (or is cancelled before the thread
        // pool dequeues the delegate), the Task.Run scheduler returns a
        // pre-cancelled task without ever running the body, which would
        // skip the finally block above and leak the
        // _activeSensations entry plus the linked CTS. Cancellation
        // propagation is already covered inside the delegate via the
        // linked.Token threaded through every Task.Delay AND the
        // ThrowIfCancellationRequested() call at the start of each
        // microsensation iteration.

        return Task.FromResult(new BackendTriggerResult(request.SensationId, totalDuration));
    }

    /// <inheritdoc />
    public Task<int> StopAsync(BackendStopRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopped = 0;

        if (request.All)
        {
            foreach (var (id, _) in _activeSensations)
            {
                if (_activeSensations.TryRemove(id, out var removed))
                {
                    // Tell the playback's catch block we own the SDK
                    // stop here, before cancelling its token. Otherwise
                    // its OCE catch may run later — possibly after a
                    // replacement sensation has already been Send()'d —
                    // and silence the new playback via OWO's global
                    // Stop semantics.
                    removed.MarkStopHandledExternally();
                    SafeCancel(removed.Cts);
                    stopped++;
                }
            }
        }
        else if (!string.IsNullOrEmpty(request.SensationId)
            && _activeSensations.TryRemove(request.SensationId, out var playback))
        {
            playback.MarkStopHandledExternally();
            SafeCancel(playback.Cts);
            stopped = 1;
        }

        // Always tell the SDK to silence the device. OWO's Stop() is a
        // global cancel of whatever's playing right now; given the
        // exclusive concurrency model this is the correct semantic for
        // both per-id and All stops.
        if (stopped > 0)
        {
            try
            {
                _sdk.Stop();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "OWO SDK Stop() threw while cancelling sensations; "
                    + "the in-process tracking has already been cleared");
            }
        }

        return Task.FromResult(stopped);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Stop the heartbeat loop before tearing down the SDK so the loop
        // doesn't observe a transient IsConnected=false during shutdown
        // and emit a spurious "transport dropped" event.
        if (_lifetimeCts is { } cts)
        {
            try
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException) { }
        }

        if (_heartbeatTask is { } heartbeat)
        {
            try
            {
                await heartbeat.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "OWO backend {Id} heartbeat threw on shutdown", Id);
            }
        }

        // Snapshot active playbacks BEFORE cancelling so we can await
        // each one's completion signal. Each playback's TCS is resolved
        // only after its SensationCancelled event is written to
        // _events — awaiting here ensures those events make the channel
        // before TryComplete shuts the writer below. We also mark each
        // playback so its OCE catch skips _sdk.Stop() — DisposeAsync
        // calls _sdk.Stop() itself further down, and unlike StopAsync
        // there's no replacement sensation to worry about, but the
        // double-Stop is still pointless noise.
        var snapshot = _activeSensations.Values.ToArray();
        foreach (var playback in snapshot)
        {
            playback.MarkStopHandledExternally();
            SafeCancel(playback.Cts);
        }
        foreach (var playback in snapshot)
        {
            try
            {
                // Bound the wait so a misbehaving SDK can't hang shutdown.
                // The playback should resolve in milliseconds once its
                // CTS is cancelled — Task.Delay observes the token
                // cancellation immediately, the catch block runs and
                // emits Cancelled, then SetResult fires.
                await playback.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogDebug(
                    "OWO backend {Id} playback didn't complete within shutdown timeout; "
                    + "continuing teardown anyway", Id);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "OWO backend {Id} playback task threw on shutdown", Id);
            }
        }
        _activeSensations.Clear();

        try
        {
            _sdk.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "OWO backend {Id} threw on Stop() during dispose; continuing", Id);
        }

        try
        {
            _sdk.Disconnect();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "OWO backend {Id} threw on Disconnect() during dispose; continuing", Id);
        }

        _events.Writer.TryComplete();
        _lifetimeCts?.Dispose();
    }

    private static ZoneTopology BuildZones()
    {
        // Mirrors MockOwoBackend.BuildZones exactly so a sensation library
        // authored against the mock backend works on the real one without
        // re-mapping. The OWO Skin's actual electrode positions are
        // approximations in body-frame coordinates; consumers should treat
        // these as hints rather than precise spatial offsets.
        var t = new ZoneTopology();
        AddZone(t, "pectoral_l", "Left pectoral", 0.4f, 0.7f, 0.3f);
        AddZone(t, "pectoral_r", "Right pectoral", 0.6f, 0.7f, 0.3f);
        AddZone(t, "abdominal_l", "Left abdominal", 0.4f, 0.5f, 0.3f);
        AddZone(t, "abdominal_r", "Right abdominal", 0.6f, 0.5f, 0.3f);
        AddZone(t, "lumbar_l", "Left lumbar", 0.4f, 0.5f, 0.7f);
        AddZone(t, "lumbar_r", "Right lumbar", 0.6f, 0.5f, 0.7f);
        AddZone(t, "dorsal_l", "Left dorsal", 0.4f, 0.7f, 0.7f);
        AddZone(t, "dorsal_r", "Right dorsal", 0.6f, 0.7f, 0.7f);
        AddZone(t, "arm_l", "Left arm", 0.2f, 0.6f, 0.5f);
        AddZone(t, "arm_r", "Right arm", 0.8f, 0.6f, 0.5f);

        AddGroup(t, "torso", "Torso",
            "pectoral_l", "pectoral_r",
            "abdominal_l", "abdominal_r",
            "lumbar_l", "lumbar_r",
            "dorsal_l", "dorsal_r");
        AddGroup(t, "arms", "Arms", "arm_l", "arm_r");
        AddGroup(t, "all", "All zones",
            "pectoral_l", "pectoral_r",
            "abdominal_l", "abdominal_r",
            "lumbar_l", "lumbar_r",
            "dorsal_l", "dorsal_r",
            "arm_l", "arm_r");
        return t;
    }

    private static void AddZone(ZoneTopology t, string id, string display, float x, float y, float z)
    {
        t.Zones.Add(new Zone
        {
            Id = id,
            DisplayName = display,
            Position = new PositionHint { X = x, Y = y, Z = z, Frame = "body" },
        });
    }

    private static void AddGroup(ZoneTopology t, string id, string display, params string[] members)
    {
        var g = new ZoneGroup { Id = id, DisplayName = display };
        foreach (var m in members)
        {
            g.ZoneIds.Add(m);
        }
        t.Groups.Add(g);
    }

    private static ParameterSchema BuildParameters()
    {
        // Mirrors MockOwoBackend.BuildParameters exactly. The OWO SDK's
        // SensationsFactory.Create() takes the same conceptual fields
        // (frequency, intensity, duration, ramp_up, ramp_down,
        // exit_delay), so a sensation file is portable between backends.
        var s = new ParameterSchema();
        s.Parameters.Add(MakeNumber("frequency", required: true, min: 1, max: 100, unit: "Hz",
            description: "Carrier frequency"));
        s.Parameters.Add(MakeNumber("intensity", required: true, min: 0, max: 100, unit: "%",
            description: "Stimulation intensity (% of calibrated maximum)"));
        s.Parameters.Add(MakeDuration("duration", required: true, min: 0, max: 10,
            description: "Active stimulation length"));
        s.Parameters.Add(MakeDuration("ramp_up", required: false, min: 0, max: 5,
            description: "Linear ramp-up before peak"));
        s.Parameters.Add(MakeDuration("ramp_down", required: false, min: 0, max: 5,
            description: "Linear ramp-down after peak"));
        s.Parameters.Add(MakeDuration("exit_delay", required: false, min: 0, max: 5,
            description: "Quiet trailing delay"));
        return s;
    }

    private static ParameterDef MakeNumber(
        string name, bool required, double min, double max, string unit, string description) =>
        new()
        {
            Name = name,
            Type = ParameterType.Number,
            Required = required,
            Min = min,
            Max = max,
            Unit = unit,
            Description = description,
        };

    private static ParameterDef MakeDuration(
        string name, bool required, double min, double max, string description) =>
        new()
        {
            Name = name,
            Type = ParameterType.Duration,
            Required = required,
            Min = min,
            Max = max,
            Description = description,
        };

    private void EmitEvent(BackendEvent evt)
    {
        if (!_events.Writer.TryWrite(evt))
        {
            _logger.LogWarning(
                "OWO backend {Id} dropped event {EventType}: channel full",
                Id, evt.GetType().Name);
        }
    }

    private static void SafeCancel(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException) { }
    }

    private OwoSendCommand BuildSendCommand(
        MicrosensationParameters micro, BackendTriggerRequest request)
    {
        var frequency = ReadNumber(micro, "frequency", defaultValue: 100);
        var duration = (float)ReadDuration(micro, "duration").TotalSeconds;
        var rampUp = (float)ReadDuration(micro, "ramp_up").TotalSeconds;
        var rampDown = (float)ReadDuration(micro, "ramp_down").TotalSeconds;
        var exitDelay = (float)ReadDuration(micro, "exit_delay").TotalSeconds;
        var intensity = ReadNumber(micro, "intensity", defaultValue: 50);

        // request.IntensityScale is a 0..100 multiplier applied at trigger
        // time (e.g. global "turn the daemon down" knob). Apply it as a
        // percentage and clamp to the OWO SDK's accepted range.
        if (request.IntensityScale.HasValue)
        {
            intensity = intensity * request.IntensityScale.Value / 100.0;
        }
        intensity = Math.Clamp(intensity, 0, 100);

        // OWO SDK takes int frequency and int intensity; round to int
        // here so the command's contract matches the SDK without any
        // narrowing happening inside StaticOwoSdk.
        return new OwoSendCommand(
            FrequencyHz: (int)Math.Round(frequency),
            DurationSeconds: duration,
            IntensityPercentage: (int)Math.Round(intensity),
            RampUpSeconds: rampUp,
            RampDownSeconds: rampDown,
            ExitDelaySeconds: exitDelay,
            ZoneIds: ExpandZones(request.ZoneIds));
    }

    /// <summary>
    /// Replace any group ids (e.g. <c>torso</c>, <c>arms</c>, <c>all</c>)
    /// with their member leaf zone ids and de-duplicate the result while
    /// preserving first-seen order. Required because <c>OwoMuscleMap</c>
    /// resolves leaves only and the upstream validator accepts groups.
    /// </summary>
    private IReadOnlyList<string> ExpandZones(IReadOnlyList<string> zoneIds)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expanded = new List<string>(zoneIds.Count);

        foreach (var id in zoneIds)
        {
            if (_zoneGroupMembers.TryGetValue(id, out var members))
            {
                foreach (var member in members)
                {
                    if (seen.Add(member))
                    {
                        expanded.Add(member);
                    }
                }
            }
            else if (seen.Add(id))
            {
                expanded.Add(id);
            }
        }

        return expanded;
    }

    private static TimeSpan ComputeEstimatedDuration(BackendTriggerRequest request)
    {
        // Microsensations play sequentially, not in parallel — sum the
        // per-step durations (active stim + envelope) so the full
        // sensation lasts the same wall-clock time as the file's declared
        // estimated_duration. Identical to MockOwoBackend so authored
        // sensations have the same total runtime on both backends.
        var total = TimeSpan.Zero;
        foreach (var micro in request.Microsensations)
        {
            total += ReadDuration(micro, "duration")
                + ReadDuration(micro, "ramp_up")
                + ReadDuration(micro, "ramp_down")
                + ReadDuration(micro, "exit_delay");
        }
        return total;
    }

    private static TimeSpan ResolveMicroDuration(MicrosensationParameters micro) =>
        ReadDuration(micro, "duration")
        + ReadDuration(micro, "ramp_up")
        + ReadDuration(micro, "ramp_down")
        + ReadDuration(micro, "exit_delay");

    private static TimeSpan ReadDuration(MicrosensationParameters micro, string key) =>
        micro.Values.TryGetValue(key, out var v) && v is ParameterValue.Duration d
            ? d.Value
            : TimeSpan.Zero;

    private static double ReadNumber(
        MicrosensationParameters micro, string key, double defaultValue) =>
        micro.Values.TryGetValue(key, out var v) && v is ParameterValue.Number n
            ? n.Value
            : defaultValue;

    /// <summary>
    /// Tracks an in-flight playback's cancellation source plus a
    /// completion task that fires only after the matching final
    /// <see cref="BackendEvent"/> (Completed or Cancelled) has been
    /// written to the event channel. <see cref="DisposeAsync"/> awaits
    /// these so shutdown doesn't drop end-of-life events.
    /// </summary>
    /// <remarks>
    /// <see cref="SkipSdkStop"/> is the load-bearing piece that prevents
    /// CANCEL_OLDEST preemption from silencing its own replacement.
    /// <c>OWO.Stop()</c> is global — it cancels whatever sensation is
    /// playing right now, regardless of which logical sensation owned
    /// it. When <see cref="StopAsync"/> or <see cref="DisposeAsync"/>
    /// handles the SDK stop itself, they flip this flag so the playback
    /// task's <c>OperationCanceledException</c> catch (which observes
    /// the cancelled token only after the thread pool gets to it, and
    /// possibly after the coordinator has already issued a replacement
    /// <c>_sdk.Send</c>) doesn't double up and stop the new sensation.
    /// Cancellations that don't go through StopAsync/DisposeAsync —
    /// e.g. the caller's gRPC token aborting mid-playback — leave this
    /// flag false so the catch still silences the device.
    /// </remarks>
    private sealed class ActivePlayback
    {
        public ActivePlayback(CancellationTokenSource cts, Task task)
        {
            Cts = cts;
            Task = task;
        }

        public CancellationTokenSource Cts { get; }

        public Task Task { get; }

        public bool SkipSdkStop { get; private set; }

        public void MarkStopHandledExternally() => SkipSdkStop = true;
    }
}
#endif

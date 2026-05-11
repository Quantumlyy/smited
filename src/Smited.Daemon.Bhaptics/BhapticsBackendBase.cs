// This file is excluded from compile on non-Windows hosts via the
// conditional <Compile Remove="BhapticsBackendBase.cs"/> ItemGroup in
// Smited.Daemon.Bhaptics.csproj. The body is additionally guarded by
// `#if WINDOWS` for IDE clarity; no Bhaptics.Tac types appear directly
// here (all SDK interaction goes through IBhapticsSdk), but the file
// is gated so cross-platform builds produce an empty assembly that
// the daemon can reflectively miss without errors.

#if WINDOWS
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Smited.Daemon.Backends;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.BodyMap;
using Smited.V1;
using ParameterValue = Smited.Daemon.Backends.Internal.ParameterValue;

namespace Smited.Daemon.Bhaptics;

/// <summary>
/// Shared per-device logic for every bHaptics backend kind
/// (<see cref="BhapticsVestBackend"/>,
/// <see cref="BhapticsSleeveBackend"/>,
/// <see cref="BhapticsFeetBackend"/>). Concrete subclasses supply the
/// device-specific identity (<see cref="DeviceKey"/>, <see cref="Kind"/>,
/// <see cref="MotorCount"/>), the zone topology, and the zone→motor
/// mapping; everything else — status tracking, heartbeat, sequence-
/// protected SDK ordering, sensation playback loop, event channel,
/// dispose — is shared.
/// </summary>
/// <remarks>
/// <para>
/// Structurally cloned from <c>Smited.Daemon.Owo.OwoBackend</c>. The
/// most important shared mechanic is the <c>_sdkSync</c> /
/// <c>_lastSendSequence</c> pattern that prevents the CANCEL_OLDEST
/// preemption race: when a stop request silences the device, every
/// concurrently-cancelling playback's <c>StopIfStillLatest</c> check
/// must observe the post-stop sequence so only the authoritative
/// caller fires <see cref="IBhapticsSdk.StopDevice"/>.
/// </para>
/// <para>
/// bHaptics is vibrotactile, not EMS — the <see cref="ParameterSchema"/>
/// advertises five parameters (<c>intensity</c>, <c>duration</c>,
/// <c>ramp_up</c>, <c>ramp_down</c>, <c>exit_delay</c>) but NO
/// <c>frequency</c>. The SDK plays a static-intensity pulse for the
/// per-microsensation <c>duration</c>; the <c>ramp_*</c> / <c>exit_delay</c>
/// envelope fields contribute to inter-microsensation wall-clock
/// spacing (and to the sensation's <c>estimated_duration</c>) but the
/// actuators are not actually attenuated during the envelope. Authors
/// who want a true ramp shape must decompose the envelope into a
/// sequence of microsensations.
/// </para>
/// </remarks>
public abstract class BhapticsBackendBase : IHapticBackend
{
    protected IBhapticsSdk Sdk { get; }
    protected TimeProvider Time { get; }
    protected ILogger Logger { get; }

    private readonly BhapticsBackendOptionsBase _options;
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
    /// Mutex for ordering <see cref="IBhapticsSdk.Submit"/> and the
    /// <see cref="IBhapticsSdk.StopDevice"/> issued from a
    /// cancellation/error catch. Same role <c>_sdkSync</c> plays in
    /// <c>OwoBackend</c>.
    /// </summary>
    private readonly object _sdkSync = new();

    /// <summary>
    /// Monotonically-increasing sequence assigned to each successful
    /// <see cref="IBhapticsSdk.Submit"/>. Read and incremented
    /// exclusively under <see cref="_sdkSync"/>.
    /// </summary>
    private long _lastSendSequence;

    private string _displayName;
    private bool _displayNameOverridden;

    protected BhapticsBackendBase(
        BhapticsBackendOptionsBase options,
        IBhapticsSdk sdk,
        TimeProvider time,
        ILogger logger,
        string defaultDisplayName,
        ZoneTopology zones,
        IReadOnlySet<BodyRegion> forbiddenRegions)
    {
        _options = options;
        Sdk = sdk;
        Time = time;
        Logger = logger;
        _displayName = defaultDisplayName;
        Zones = zones;
        ForbiddenRegions = forbiddenRegions;

        Parameters = BhapticsBackendParameters.Build();
        Concurrency = new ConcurrencyModel
        {
            MaxConcurrent = 1,
            Policy = ConcurrencyPolicy.CancelOldest,
        };

        // Memoize group membership so trigger-time expansion is just a
        // dictionary lookup. The validator upstream accepts any zone id
        // advertised in Zones (leaves AND groups), but BhapticsMotorMap
        // resolves leaves only — we have to expand groups here before
        // the motor map sees them.
        _zoneGroupMembers = Zones.Groups.ToDictionary(
            g => g.Id,
            g => (IReadOnlyList<string>)g.ZoneIds.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The smited-side device key passed to
    /// <see cref="IBhapticsSdk"/> (<c>"vest" | "sleeve_l" | "sleeve_r" |
    /// "feet_l" | "feet_r"</c>).</summary>
    public abstract string DeviceKey { get; }

    /// <summary>Number of motors on this device (40 vest, 6 sleeve,
    /// 3 feet).</summary>
    public abstract int MotorCount { get; }

    /// <summary>Map a smited zone id to the motor indices it activates
    /// on this device. Returns an empty array for unknown zones
    /// (silently ignored at trigger time).</summary>
    protected abstract IReadOnlyList<int> MotorsForZone(string zoneId);

    /// <inheritdoc />
    public string Id => _options.BackendId;

    /// <inheritdoc />
    public abstract string Kind { get; }

    /// <inheritdoc />
    public string DisplayName => _displayName;

    /// <summary>
    /// Replaces the default <see cref="DisplayName"/> with a per-descriptor
    /// override. One-shot per instance; a conflicting second override
    /// throws so a misconfiguration that lands two descriptors at the
    /// same backend instance can't silently clobber the first override.
    /// Mirrors <c>OwoBackend.OverrideDisplayName</c>.
    /// </summary>
    internal void OverrideDisplayName(string displayName)
    {
        ArgumentException.ThrowIfNullOrEmpty(displayName);
        if (_displayNameOverridden && !string.Equals(_displayName, displayName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"BhapticsBackend.DisplayName was already overridden to '{_displayName}'; "
                + $"cannot re-override to '{displayName}'.");
        }
        _displayName = displayName;
        _displayNameOverridden = true;
    }

    /// <inheritdoc />
    public BackendStatus Status { get; private set; } = BackendStatus.Disconnected;

    /// <inheritdoc />
    public IReadOnlyList<string> Capabilities { get; } = new[]
    {
        // No "ems" capability: bHaptics is vibrotactile. "vibrotactile"
        // is the analogous descriptor. "zoned" and "calibrated" carry
        // over from OWO since the device exposes named zones and
        // pairing through the Player implies calibration.
        "vibrotactile", "zoned", "calibrated",
    };

    /// <inheritdoc />
    public ZoneTopology Zones { get; }

    /// <inheritdoc />
    public ParameterSchema Parameters { get; }

    /// <inheritdoc />
    public ConcurrencyModel Concurrency { get; }

    /// <inheritdoc />
    public CalibrationState? Calibration { get; private set; }

    /// <inheritdoc />
    public Struct? Extras => null;

    /// <inheritdoc />
    public IReadOnlySet<BodyRegion> ForbiddenRegions { get; }

    /// <inheritdoc />
    public IAsyncEnumerable<BackendEvent> Events => _events.Reader.ReadAllAsync();

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken ct)
    {
        Status = BackendStatus.Disconnected;

        try
        {
            // Idempotent across all backends sharing the SDK — only the
            // first call actually opens the WebSocket.
            await Sdk.InitializeAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Status = BackendStatus.Disconnected;
            throw;
        }
        catch (Exception ex)
        {
            Status = BackendStatus.Error;
            Logger.LogError(ex,
                "bHaptics backend {Id} failed to initialize SDK; ensure the bHaptics Player is installed",
                Id);
            throw;
        }

        // Per-device readiness is a separate signal: the SDK may have
        // opened but the device may not be paired yet. Best-effort
        // observe; missing-device is not a startup error — the
        // heartbeat will flip Status to Ready when the device shows up.
        var deviceConnected = Sdk.IsDeviceConnected(DeviceKey);
        _lastSeenConnected = deviceConnected;
        if (deviceConnected)
        {
            Status = BackendStatus.Ready;
            // The Player handles pairing/calibration; pairing imply
            // calibration the same way MyOWO does for OWO. Stamp connect
            // time as a best-effort approximation since the SDK does
            // not expose the firmware-side calibration timestamp.
            Calibration = new CalibrationState
            {
                Calibrated = true,
                LastCalibratedAt = Timestamp.FromDateTimeOffset(Time.GetUtcNow()),
            };
            Logger.LogInformation(
                "bHaptics backend {Id} connected to device {DeviceKey} and ready", Id, DeviceKey);
        }
        else
        {
            Logger.LogInformation(
                "bHaptics backend {Id} initialized; device {DeviceKey} not yet paired, awaiting heartbeat",
                Id, DeviceKey);
        }

        StartHeartbeat();
    }

    private void StartHeartbeat()
    {
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
                await Task.Delay(period, Time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            bool connected;
            try
            {
                connected = Sdk.IsDeviceConnected(DeviceKey);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex,
                    "bHaptics backend {Id} heartbeat failed reading IsDeviceConnected", Id);
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
                if (Calibration is null)
                {
                    Calibration = new CalibrationState
                    {
                        Calibrated = true,
                        LastCalibratedAt = Timestamp.FromDateTimeOffset(Time.GetUtcNow()),
                    };
                }
                EmitLifecycleStatusChanged("device connected");
                Logger.LogInformation("bHaptics backend {Id} device {DeviceKey} connected", Id, DeviceKey);
            }
            else
            {
                Status = BackendStatus.Disconnected;
                EmitLifecycleStatusChanged("device disconnected");
                Logger.LogWarning(
                    "bHaptics backend {Id} device {DeviceKey} dropped; the Player auto-reconnects, "
                    + "waiting for it to come back",
                    Id, DeviceKey);
                // Unlike OWO, the bHaptics Player handles reconnection
                // internally — no explicit TryReconnectAsync loop is
                // required. The heartbeat keeps polling; when the device
                // re-pairs we flip back to Ready on the next tick. We
                // still respect MaxReconnectAttempts as a "give up
                // eventually and flip to Error" budget so persistent
                // hardware faults don't sit in Disconnected forever.
                if (_options.MaxReconnectAttempts > 0)
                {
                    _ = WaitForReconnectAsync(ct);
                }
            }
        }
    }

    private async Task WaitForReconnectAsync(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= _options.MaxReconnectAttempts; attempt++)
        {
            try
            {
                var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                await Task.Delay(backoff, Time, ct).ConfigureAwait(false);
                if (Sdk.IsDeviceConnected(DeviceKey))
                {
                    Logger.LogInformation(
                        "bHaptics backend {Id} device {DeviceKey} re-paired on attempt {Attempt}",
                        Id, DeviceKey, attempt);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex,
                    "bHaptics backend {Id} reconnect check {Attempt} threw", Id, attempt);
            }
        }

        Status = BackendStatus.Error;
        EmitLifecycleStatusChanged("reconnection exhausted");
        Logger.LogError(
            "bHaptics backend {Id} device {DeviceKey} did not re-pair after {Max} attempts; restart the daemon to retry",
            Id, DeviceKey, _options.MaxReconnectAttempts);
    }

    private void EmitLifecycleStatusChanged(string reason) =>
        EmitEvent(new BackendLifecycleEvent(
            Id,
            Time.GetUtcNow(),
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
                $"bHaptics backend {Id} status is {Status}, cannot trigger");
        }

        var totalDuration = ComputeEstimatedDuration(request);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playback = new ActivePlayback(linked, completion.Task);
        _activeSensations[request.SensationId] = playback;

        EmitEvent(new SensationStarted(
            Id,
            Time.GetUtcNow(),
            request.SensationId,
            request.SensationName,
            request.ClientTraceId));

        Logger.LogInformation(
            "bHaptics backend {Id} firing {SensationId} ({SensationName}) on {Zones} for {Duration}",
            Id,
            request.SensationId,
            request.SensationName ?? "<inline>",
            string.Join(",", request.ZoneIds),
            totalDuration);

        var totalDelay = totalDuration > TimeSpan.Zero
            ? Task.Delay(totalDuration, Time, linked.Token)
            : Task.CompletedTask;

        _ = Task.Run(async () =>
        {
            BackendEvent finalEvent;
            long mySequence = 0;
            try
            {
                for (var i = 0; i < request.Microsensations.Count; i++)
                {
                    linked.Token.ThrowIfCancellationRequested();

                    var micro = request.Microsensations[i];
                    var motorPayload = BuildMotorPayload(request, micro);
                    var durationMs = (int)Math.Round(ReadDuration(micro, "duration").TotalMilliseconds);
                    // Clamp at 1ms so a sensation with a 0-duration
                    // microsensation (envelope-only step) still issues
                    // a Submit and the Player can record it. Zero would
                    // be rejected by the SDK.
                    if (durationMs < 1) durationMs = 1;
                    mySequence = SubmitAndStamp(motorPayload, durationMs, linked.Token);

                    if (i < request.Microsensations.Count - 1)
                    {
                        var thisDuration = ResolveMicroDuration(micro);
                        if (thisDuration > TimeSpan.Zero)
                        {
                            await Task.Delay(thisDuration, Time, linked.Token).ConfigureAwait(false);
                        }
                    }
                }

                await totalDelay.ConfigureAwait(false);

                finalEvent = new SensationCompleted(
                    Id,
                    Time.GetUtcNow(),
                    request.SensationId,
                    request.SensationName,
                    request.ClientTraceId);
            }
            catch (OperationCanceledException)
            {
                if (mySequence != 0)
                {
                    StopDeviceIfStillLatest(mySequence);
                }
                finalEvent = new SensationCancelled(
                    Id,
                    Time.GetUtcNow(),
                    request.SensationId,
                    request.SensationName,
                    request.ClientTraceId,
                    Reason: "stopped");
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex,
                    "bHaptics sensation {SensationId} failed mid-flight",
                    request.SensationId);
                if (mySequence != 0)
                {
                    StopDeviceIfStillLatest(mySequence);
                }
                finalEvent = new SensationCancelled(
                    Id,
                    Time.GetUtcNow(),
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
            completion.TrySetResult();
        });

        return Task.FromResult(new BackendTriggerResult(request.SensationId, totalDuration));
    }

    /// <inheritdoc />
    public Task<int> StopAsync(BackendStopRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopped = 0;
        var toCancel = new List<ActivePlayback>();

        if (request.All)
        {
            foreach (var (id, _) in _activeSensations)
            {
                if (_activeSensations.TryRemove(id, out var removed))
                {
                    toCancel.Add(removed);
                    stopped++;
                }
            }
        }
        else if (!string.IsNullOrEmpty(request.SensationId)
            && _activeSensations.TryRemove(request.SensationId, out var playback))
        {
            toCancel.Add(playback);
            stopped = 1;
        }

        if (stopped > 0)
        {
            StopDeviceAuthoritatively(toCancel);
        }

        return Task.FromResult(stopped);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
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
                Logger.LogDebug(ex, "bHaptics backend {Id} heartbeat threw on shutdown", Id);
            }
        }

        var snapshot = _activeSensations.Values.ToArray();
        // Authoritative stop on this device only — the SDK is shared
        // with other backends, we don't want to silence their devices.
        StopDeviceAuthoritatively(snapshot);

        foreach (var playback in snapshot)
        {
            try
            {
                await playback.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Logger.LogDebug(
                    "bHaptics backend {Id} playback didn't complete within shutdown timeout; "
                    + "continuing teardown anyway", Id);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex,
                    "bHaptics backend {Id} playback task threw on shutdown", Id);
            }
        }
        _activeSensations.Clear();

        // We do NOT call Sdk.DisposeAsync here — the SDK is a daemon-wide
        // singleton shared across every bhaptics_* backend, and disposing
        // it would tear down the WebSocket for the others. The host DI
        // container disposes the SDK once at shutdown.

        _events.Writer.TryComplete();
        _lifetimeCts?.Dispose();
    }

    /// <summary>
    /// Build the per-motor intensity array for this device's
    /// <see cref="MotorCount"/>. Reads <c>intensity</c> from the
    /// microsensation (default 50), applies the request's
    /// <see cref="BackendTriggerRequest.IntensityScale"/> multiplier
    /// if present, clamps to 0..100, then for each zone in the
    /// request writes the byte value into every motor the zone
    /// activates. Multiple zones hitting the same motor take the
    /// max (closer to user intent than sum-and-clamp).
    /// </summary>
    protected byte[] BuildMotorPayload(
        BackendTriggerRequest request, MicrosensationParameters micro)
    {
        var payload = new byte[MotorCount];
        var intensity = ReadNumber(micro, "intensity", defaultValue: 50);
        if (request.IntensityScale.HasValue)
        {
            intensity = intensity * request.IntensityScale.Value / 100.0;
        }
        intensity = Math.Clamp(intensity, 0, 100);
        var intensityByte = (byte)Math.Round(intensity);

        foreach (var zoneId in ExpandZones(request.ZoneIds))
        {
            foreach (var motorIdx in MotorsForZone(zoneId))
            {
                if (motorIdx < 0 || motorIdx >= MotorCount) continue;
                if (payload[motorIdx] < intensityByte)
                {
                    payload[motorIdx] = intensityByte;
                }
            }
        }

        return payload;
    }

    /// <summary>
    /// Replace any group ids with their member leaf zone ids and
    /// de-duplicate while preserving first-seen order. Required
    /// because <see cref="MotorsForZone"/> resolves leaves only and
    /// the upstream validator accepts groups.
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
    /// Submit a motor payload to the SDK and return the sequence
    /// number assigned to it. Re-checks <paramref name="ct"/> under
    /// <see cref="_sdkSync"/> before issuing the Submit, so a
    /// <see cref="StopAsync"/> / <see cref="DisposeAsync"/> that
    /// initiated cancellation while we were waiting for the lock
    /// aborts before touching the device.
    /// </summary>
    internal long SubmitAndStamp(byte[] motorPayload, int durationMs, CancellationToken ct)
    {
        lock (_sdkSync)
        {
            ct.ThrowIfCancellationRequested();
            Sdk.Submit(DeviceKey, motorPayload, durationMs);
            var sequence = ++_lastSendSequence;
            Logger.LogDebug(
                "bHaptics Submit dispatched: backend_id={BackendId} device={DeviceKey} sequence={Sequence}",
                Id, DeviceKey, sequence);
            return sequence;
        }
    }

    internal bool StopDeviceIfStillLatest(long capturedSequence)
    {
        lock (_sdkSync)
        {
            if (_lastSendSequence != capturedSequence)
            {
                Logger.LogDebug(
                    "bHaptics StopDevice suppressed: backend_id={BackendId} captured_sequence={Captured} superseded by {Latest}",
                    Id, capturedSequence, _lastSendSequence);
                return false;
            }
            try
            {
                Sdk.StopDevice(DeviceKey);
                Logger.LogDebug(
                    "bHaptics StopDevice fired: backend_id={BackendId} sequence={Sequence}",
                    Id, capturedSequence);
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex,
                    "bHaptics SDK StopDevice threw during sequence-protected catch");
                return false;
            }
        }
    }

    internal void StopDeviceAuthoritatively() =>
        StopDeviceAuthoritatively(Array.Empty<ActivePlayback>());

    private void StopDeviceAuthoritatively(IReadOnlyCollection<ActivePlayback> playbacksToCancel)
    {
        lock (_sdkSync)
        {
            try
            {
                Sdk.StopDevice(DeviceKey);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex,
                    "bHaptics SDK StopDevice threw during authoritative stop");
            }
            var newSeq = ++_lastSendSequence;
            Logger.LogDebug(
                "bHaptics authoritative StopDevice issued: backend_id={BackendId} sequence advanced to {Seq} cancelling {Count} playback(s)",
                Id, newSeq, playbacksToCancel.Count);

            foreach (var playback in playbacksToCancel)
            {
                SafeCancel(playback.Cts);
            }
        }
    }

    private void EmitEvent(BackendEvent evt)
    {
        if (!_events.Writer.TryWrite(evt))
        {
            Logger.LogWarning(
                "bHaptics backend {Id} dropped event {EventType}: channel full",
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

    /// <summary>
    /// Tracks an in-flight playback's cancellation source plus a
    /// completion task that fires only after the matching final
    /// <see cref="BackendEvent"/> has been written to the event
    /// channel.
    /// </summary>
    private sealed record ActivePlayback(CancellationTokenSource Cts, Task Task);
}
#endif

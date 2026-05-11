using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.BodyMap;
using Smited.V1;
using ParameterValue = Smited.Daemon.Backends.Internal.ParameterValue;

namespace Smited.Daemon.Backends.Mock;

/// <summary>
/// Shared in-process simulation logic for every mock bHaptics backend
/// kind (<see cref="MockBhapticsVestBackend"/>,
/// <see cref="MockBhapticsSleeveBackend"/>,
/// <see cref="MockBhapticsFeetBackend"/>). Mirrors
/// <see cref="MockOwoBackend"/>'s shape — fast, deterministic when
/// driven by <c>FakeTimeProvider</c>, with the addition of a
/// motor-payload capture surface (<see cref="RecentSubmissions"/>) so
/// cross-platform tests can assert against the exact bytes the
/// backend would have submitted to the real bHaptics Player.
/// </summary>
public abstract class MockBhapticsBackendBase : IHapticBackend, IMockBhapticsController
{
    private static readonly DateTimeOffset SeedCalibratedAt =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Channel<BackendEvent> _events = Channel.CreateUnbounded<BackendEvent>();
    private readonly ConcurrentDictionary<string, ActivePlayback> _playbacks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _submissionsLock = new();
    private readonly LinkedList<MockBhapticsSubmission> _submissions = new();
    private const int SubmissionsCap = 100;

    private CalibrationState _calibration;
    private string _id;
    private readonly string _defaultId;
    private string _displayName;
    private readonly string _defaultDisplayName;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _zoneGroupMembers;

    protected MockBhapticsBackendBase(
        TimeProvider time,
        ILogger logger,
        string defaultId,
        string defaultDisplayName,
        ZoneTopology zones,
        IReadOnlySet<BodyRegion> forbiddenRegions)
    {
        _time = time;
        _logger = logger;
        _defaultId = defaultId;
        _id = defaultId;
        _defaultDisplayName = defaultDisplayName;
        _displayName = defaultDisplayName;
        Zones = zones;
        ForbiddenRegions = forbiddenRegions;
        Parameters = BhapticsBackendParameters.Build();
        Concurrency = new ConcurrencyModel
        {
            MaxConcurrent = 1,
            Policy = ConcurrencyPolicy.CancelOldest,
        };
        _calibration = new CalibrationState
        {
            Calibrated = true,
            LastCalibratedAt = Timestamp.FromDateTimeOffset(SeedCalibratedAt),
        };
        _zoneGroupMembers = Zones.Groups.ToDictionary(
            g => g.Id,
            g => (IReadOnlyList<string>)g.ZoneIds.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Smited-side device key (<c>"vest" | "sleeve_l" | …</c>).
    /// Captured into <see cref="MockBhapticsSubmission.DeviceKey"/> so
    /// tests can disambiguate which mock received the submission.</summary>
    public abstract string DeviceKey { get; }

    /// <summary>Number of motors on this device.</summary>
    public abstract int MotorCount { get; }

    /// <summary>Map a smited zone id to the motor indices it activates
    /// on this device.</summary>
    protected abstract IReadOnlyList<int> MotorsForZone(string zoneId);

    public string Id => _id;
    public abstract string Kind { get; }
    public string DisplayName => _displayName;

    /// <summary>Replaces the default <see cref="Id"/> with a per-descriptor
    /// override at startup. One-shot per instance.</summary>
    internal void OverrideId(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (string.Equals(_id, id, StringComparison.Ordinal)) return;
        if (!string.Equals(_id, _defaultId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{GetType().Name}.Id was already overridden to '{_id}'; cannot re-override to '{id}'. "
                + $"Configure at most one {Kind} descriptor.");
        }
        _id = id;
    }

    internal void OverrideDisplayName(string displayName)
    {
        ArgumentException.ThrowIfNullOrEmpty(displayName);
        if (string.Equals(_displayName, displayName, StringComparison.Ordinal)) return;
        if (!string.Equals(_displayName, _defaultDisplayName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{GetType().Name}.DisplayName was already overridden to '{_displayName}'; "
                + $"cannot re-override to '{displayName}'.");
        }
        _displayName = displayName;
    }

    public BackendStatus Status => BackendStatus.Ready;

    public IReadOnlyList<string> Capabilities { get; } = new[]
    {
        "vibrotactile", "zoned", "calibrated", "sensation_registry_mutable",
    };

    public ZoneTopology Zones { get; }
    public ParameterSchema Parameters { get; }
    public ConcurrencyModel Concurrency { get; }
    public CalibrationState? Calibration => _calibration;
    public Struct? Extras => null;
    public IReadOnlySet<BodyRegion> ForbiddenRegions { get; }
    public IAsyncEnumerable<BackendEvent> Events => _events.Reader.ReadAllAsync();

    public IReadOnlyCollection<string> ActiveSensationIds => _playbacks.Keys.ToArray();

    public MicrosensationParameters BuildDiagnosticMicrosensation() =>
        new(new Dictionary<string, ParameterValue>
        {
            ["intensity"] = new ParameterValue.Number(60),
            ["duration"] = new ParameterValue.Duration(TimeSpan.FromMilliseconds(300)),
        });

    public IReadOnlyList<MockBhapticsSubmission> RecentSubmissions
    {
        get
        {
            lock (_submissionsLock)
            {
                return _submissions.ToArray();
            }
        }
    }

    public void ClearSubmissions()
    {
        lock (_submissionsLock)
        {
            _submissions.Clear();
        }
    }

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
            request.ClientTraceId,
            request.ZoneIds,
            request.IntensityScale));

        _logger.LogInformation(
            "Mock bHaptics {DeviceKey} firing {SensationId} ({SensationName}) on {Zones} for {Duration}",
            DeviceKey,
            request.SensationId,
            request.SensationName ?? "<inline>",
            string.Join(",", request.ZoneIds),
            estimated);

        // Capture every microsensation's payload synchronously so a test
        // that triggers and immediately reads RecentSubmissions sees the
        // submission. The OWO mock didn't need this because its real
        // backend's Send signature was OWO-specific and there was no
        // bytes-on-wire surface to assert on.
        foreach (var micro in request.Microsensations)
        {
            var payload = BuildMotorPayload(request, micro);
            var durationMs = (int)Math.Round(ReadDuration(micro, "duration").TotalMilliseconds);
            if (durationMs < 1) durationMs = 1;
            AppendSubmission(new MockBhapticsSubmission(
                DeviceKey,
                payload.ToImmutableArray(),
                TimeSpan.FromMilliseconds(durationMs),
                _time.GetUtcNow()));
        }

        // Pre-register the delay against _time so FakeTimeProvider
        // tests can advance time deterministically right after
        // TriggerAsync returns.
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
        else if (!string.IsNullOrEmpty(request.SensationId)
            && _playbacks.TryRemove(request.SensationId, out var p))
        {
            SafeCancel(p.Cts);
            stopped++;
        }
        return Task.FromResult(stopped);
    }

    public void SetCalibrated(bool calibrated, DateTimeOffset? at = null)
    {
        var stamp = at ?? _time.GetUtcNow();
        _calibration = new CalibrationState
        {
            Calibrated = calibrated,
            LastCalibratedAt = Timestamp.FromDateTimeOffset(stamp),
        };
        EmitEvent(new CalibrationChangedEvent(Id, _time.GetUtcNow(), _calibration));
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

    /// <summary>
    /// Build the per-motor intensity array for this device. Identical
    /// algorithm to <c>BhapticsBackendBase.BuildMotorPayload</c> so the
    /// mock and real backend produce bit-identical payloads for the
    /// same input.
    /// </summary>
    private byte[] BuildMotorPayload(
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
                    if (seen.Add(member)) expanded.Add(member);
                }
            }
            else if (seen.Add(id))
            {
                expanded.Add(id);
            }
        }
        return expanded;
    }

    private void AppendSubmission(MockBhapticsSubmission submission)
    {
        lock (_submissionsLock)
        {
            _submissions.AddLast(submission);
            while (_submissions.Count > SubmissionsCap)
            {
                _submissions.RemoveFirst();
            }
        }
    }

    private void EmitEvent(BackendEvent evt) => _events.Writer.TryWrite(evt);

    private static void SafeCancel(CancellationTokenSource cts)
    {
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
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

    private static TimeSpan ReadDuration(MicrosensationParameters micro, string key) =>
        micro.Values.TryGetValue(key, out var v) && v is ParameterValue.Duration d
            ? d.Value
            : TimeSpan.Zero;

    private static double ReadNumber(MicrosensationParameters micro, string key, double defaultValue) =>
        micro.Values.TryGetValue(key, out var v) && v is ParameterValue.Number n
            ? n.Value
            : defaultValue;

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

using System.Collections.Concurrent;
using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Smited.Daemon.Backends.Internal;
using Smited.V1;
using ParameterValue = Smited.Daemon.Backends.Internal.ParameterValue;

namespace Smited.Daemon.Backends.Mock;

/// <summary>
/// Faithful in-process simulation of the OWO Skin haptic vest. Mirrors the
/// real backend's zone topology, parameter schema and concurrency model
/// closely enough that the gRPC surface, event streaming and concurrency
/// behaviour can be exercised end-to-end on Mac without hardware. Timing
/// is deterministic given a <see cref="TimeProvider"/> — tests inject
/// <c>FakeTimeProvider</c> to fast-forward.
/// </summary>
public sealed class MockOwoBackend : IHapticBackend, IMockOwoController
{
    private static readonly DateTimeOffset SeedCalibratedAt =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly TimeProvider _time;
    private readonly ILogger<MockOwoBackend> _logger;
    private readonly Channel<BackendEvent> _events = Channel.CreateUnbounded<BackendEvent>();
    private readonly ConcurrentDictionary<string, ActivePlayback> _playbacks =
        new(StringComparer.OrdinalIgnoreCase);

    private CalibrationState _calibration;
    private string _id = "mock-owo";
    private string _displayName = "Mock OWO Skin";

    public MockOwoBackend(TimeProvider time, ILogger<MockOwoBackend> logger)
    {
        _time = time;
        _logger = logger;
        Zones = BuildZones();
        Parameters = BuildParameters();
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
    }

    public string Id => _id;

    public string Kind => "owo_skin";

    public string DisplayName => _displayName;

    /// <summary>
    /// Replaces the default <see cref="Id"/> with a per-descriptor
    /// override at startup. Idempotent for the same value; throws on a
    /// conflicting second override so the descriptor validator's
    /// "single mock_owo descriptor" rule isn't silently bypassed when
    /// it fails to fire.
    /// </summary>
    internal void OverrideId(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (string.Equals(_id, id, StringComparison.Ordinal))
        {
            return;
        }
        if (!string.Equals(_id, "mock-owo", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"MockOwoBackend.Id was already overridden to '{_id}'; cannot re-override to '{id}'. "
                + "Configure at most one mock_owo descriptor.");
        }
        _id = id;
    }

    /// <summary>
    /// Replaces the default <see cref="DisplayName"/> with a
    /// per-descriptor override. Same idempotency / single-override
    /// rules as <see cref="OverrideId"/>.
    /// </summary>
    internal void OverrideDisplayName(string displayName)
    {
        ArgumentException.ThrowIfNullOrEmpty(displayName);
        if (string.Equals(_displayName, displayName, StringComparison.Ordinal))
        {
            return;
        }
        if (!string.Equals(_displayName, "Mock OWO Skin", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"MockOwoBackend.DisplayName was already overridden to '{_displayName}'; cannot re-override to '{displayName}'. "
                + "Configure at most one mock_owo descriptor.");
        }
        _displayName = displayName;
    }

    public BackendStatus Status => BackendStatus.Ready;

    public IReadOnlyList<string> Capabilities { get; } = new[]
    {
        "ems", "zoned", "calibrated", "sensation_registry_mutable",
    };

    public ZoneTopology Zones { get; }

    public ParameterSchema Parameters { get; }

    public ConcurrencyModel Concurrency { get; }

    public CalibrationState? Calibration => _calibration;

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
            "Mock OWO firing {SensationId} ({SensationName}) on {Zones} for {Duration}",
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
                // Dispose the linked CTS we created above so its registration
                // on the caller's token is released — otherwise long-lived
                // callers accumulate registrations once per trigger.
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
            foreach (var (id, playback) in _playbacks)
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
        // Microsensations play sequentially, not in parallel — sum the
        // per-step durations (active stim + envelope) so the full
        // sensation lasts the same wall-clock time as the file's
        // declared estimated_duration. Taking the max would let
        // multi-pulse sensations like test_failed complete after just
        // the longest pulse and release the concurrency slot too early.
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
        micro.Values.TryGetValue(key, out var v) && v is ParameterValue.Duration d ? d.Value : TimeSpan.Zero;

    private static ZoneTopology BuildZones()
    {
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
        var s = new ParameterSchema();
        s.Parameters.Add(MakeNumber("frequency", required: true, min: 1, max: 100, unit: "Hz",
            description: "Carrier frequency"));
        s.Parameters.Add(MakeNumber("intensity", required: true, min: 0, max: 100, unit: "%",
            description: "Stimulation intensity"));
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

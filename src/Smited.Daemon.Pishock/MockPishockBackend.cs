using System.Collections.Immutable;
using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Smited.Daemon.Backends;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.BodyMap;
using Smited.Daemon.Pishock.Internal;
using Smited.V1;
using ParameterValue = Smited.Daemon.Backends.Internal.ParameterValue;

namespace Smited.Daemon.Pishock;

/// <summary>
/// In-process simulation of a PiShock device. Validates triggers
/// against the descriptor's <see cref="PishockBackendOptions"/> the
/// same way the real backend does (op allow-list, per-op intensity
/// caps, duration cap, token-bucket rate limit) and logs each fired op
/// at INFO. No real HTTP traffic; works on every supported host.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StopAsync"/> is a documented no-op: PiShock's wire
/// protocol has no "cancel an in-progress op" message, so neither the
/// real nor the mock backend can interrupt an op once it's fired. The
/// daemon waits for the op's duration to elapse instead.
/// </para>
/// <para>
/// Each instance is independent — no static SDK, no shared state. A
/// daemon can run multiple <c>mock_pishock</c> descriptors side by
/// side, mirroring the multi-shocker physical setup.
/// </para>
/// </remarks>
public sealed class MockPishockBackend : IHapticBackend
{
    private static readonly IReadOnlySet<BodyRegion> ManufacturerForbidden =
        ImmutableHashSet.Create(
            BodyRegion.Head,
            BodyRegion.Face,
            BodyRegion.Throat,
            BodyRegion.Neck,
            BodyRegion.ChestFront,
            BodyRegion.ChestOverHeart,
            BodyRegion.BackUpper,
            BodyRegion.BackLower);

    private readonly PishockBackendOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<MockPishockBackend> _logger;
    private readonly Channel<BackendEvent> _events = Channel.CreateUnbounded<BackendEvent>();
    private readonly TokenBucket _bucket;

    public MockPishockBackend(
        string id,
        PishockBackendOptions options,
        TimeProvider time,
        ILogger<MockPishockBackend> logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        Id = id;
        _options = options;
        _time = time;
        _logger = logger;

        DisplayName = !string.IsNullOrEmpty(options.DisplayName) ? options.DisplayName : id;
        Capabilities = BuildCapabilities(options.AllowedOps);
        Zones = BuildZones();
        Parameters = BuildParameters(options.AllowedOps);
        Concurrency = new ConcurrencyModel
        {
            MaxConcurrent = 1,
            Policy = ConcurrencyPolicy.CancelOldest,
        };
        _bucket = new TokenBucket(options.MaxBurst, options.MaxOpsPerSecond, time);
    }

    public string Id { get; }

    public string Kind => "pishock";

    public string DisplayName { get; }

    public BackendStatus Status => BackendStatus.Ready;

    public IReadOnlyList<string> Capabilities { get; }

    public ZoneTopology Zones { get; }

    public ParameterSchema Parameters { get; }

    public ConcurrencyModel Concurrency { get; }

    /// <inheritdoc />
    /// <remarks>
    /// PiShock has no calibration phase — safety lives in the per-trigger
    /// intensity ceiling and the manufacturer-forbidden region set, both
    /// of which are checked at admission time without device interaction.
    /// </remarks>
    public CalibrationState? Calibration => null;

    public Struct? Extras => null;

    public IReadOnlySet<BodyRegion> ForbiddenRegions => ManufacturerForbidden;

    public IAsyncEnumerable<BackendEvent> Events => _events.Reader.ReadAllAsync();

    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<BackendTriggerResult> TriggerAsync(
        BackendTriggerRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Per-instance validation that ParameterSchema can't fully express
        // (per-op intensity caps, the AllowedOps allow-list at runtime in
        // case the schema is bypassed). Reject the whole trigger upfront so
        // the user sees one INVALID_PARAMETER instead of a partially fired
        // sequence.
        for (var i = 0; i < request.Microsensations.Count; i++)
        {
            ValidateMicrosensation(i, request.Microsensations[i]);
        }

        // Pre-allocate one bucket token per microsensation. Atomic: if the
        // sequence can't fit in the current bucket, refund nothing and
        // reject the whole trigger. Partial fires would silently drop
        // pulses the user authored.
        for (var i = 0; i < request.Microsensations.Count; i++)
        {
            if (!_bucket.TryConsume())
            {
                throw new BackendTriggerRejectedException(
                    TriggerErrorCode.RateLimited,
                    $"trigger needs {request.Microsensations.Count} bucket tokens but "
                    + $"only {i} were available; bump MaxBurst or slow down the trigger rate",
                    "rate_limit");
            }
        }

        var estimated = ComputeEstimatedDuration(request);

        EmitEvent(new SensationStarted(
            Id, _time.GetUtcNow(),
            request.SensationId, request.SensationName, request.ClientTraceId));

        // Log each microsensation up-front with its scheduled offset.
        // Logging during the playback task would be more faithful to the
        // wire timing but introduces a race window between Task.Run
        // scheduling and FakeTimeProvider.Advance — see the comment on
        // the Task.Delay pre-creation below.
        LogPulses(request);

        // Pre-create the Task.Delay synchronously so its timer is
        // registered with `_time` before TriggerAsync returns. Otherwise
        // a test that calls FakeTimeProvider.Advance immediately after
        // Trigger races the Task.Run scheduling, the delay registers
        // *after* the advance, and SensationCompleted never fires within
        // the test's expected window.
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
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
                    Id, _time.GetUtcNow(),
                    request.SensationId, request.SensationName, request.ClientTraceId);
            }
            catch (OperationCanceledException)
            {
                finalEvent = new SensationCancelled(
                    Id, _time.GetUtcNow(),
                    request.SensationId, request.SensationName, request.ClientTraceId,
                    Reason: "preempted_or_stopped");
            }
            finally
            {
                linked.Dispose();
            }
            EmitEvent(finalEvent);
        });

        return Task.FromResult(new BackendTriggerResult(request.SensationId, estimated));
    }

    /// <inheritdoc />
    /// <remarks>
    /// PiShock's wire protocol has no "cancel an in-progress op" message
    /// — both the cloud API and the LAN firmware fire-and-forget. The
    /// mock matches the wire so behavior is consistent across mock and
    /// real backends. The coordinator's preempt path cancels the
    /// sensation's CTS, freeing the concurrency slot, but the device
    /// continues to fire the in-progress op until its authored duration
    /// elapses.
    /// </remarks>
    public Task<int> StopAsync(BackendStopRequest request, CancellationToken ct) =>
        Task.FromResult(0);

    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private void EmitEvent(BackendEvent evt) => _events.Writer.TryWrite(evt);

    private void ValidateMicrosensation(int index, MicrosensationParameters micro)
    {
        var op = ReadOp(micro);
        if (!_options.AllowedOps.Contains(op))
        {
            throw new BackendTriggerRejectedException(
                TriggerErrorCode.InvalidParameter,
                $"op '{op}' is not in this descriptor's AllowedOps "
                + $"({string.Join(", ", _options.AllowedOps)})",
                $"microsensations[{index}].parameters.op");
        }

        var intensity = (int)ReadNumber(micro, "intensity");
        var cap = op switch
        {
            PishockOp.Shock => _options.MaxIntensityShock,
            PishockOp.Vibrate => _options.MaxIntensityVibrate,
            PishockOp.Beep => 100,
            _ => 0,
        };
        if (intensity > cap)
        {
            throw new BackendTriggerRejectedException(
                TriggerErrorCode.InvalidParameter,
                $"intensity {intensity} exceeds {op} cap of {cap}",
                $"microsensations[{index}].parameters.intensity");
        }

        var duration = ReadDuration(micro, "duration");
        var maxDuration = TimeSpan.FromMilliseconds(_options.MaxDurationMs);
        if (duration > maxDuration)
        {
            throw new BackendTriggerRejectedException(
                TriggerErrorCode.InvalidParameter,
                $"duration {duration.TotalMilliseconds}ms exceeds cap of {_options.MaxDurationMs}ms",
                $"microsensations[{index}].parameters.duration");
        }
    }

    private void LogPulses(BackendTriggerRequest request)
    {
        var offset = TimeSpan.Zero;
        for (var i = 0; i < request.Microsensations.Count; i++)
        {
            var micro = request.Microsensations[i];
            offset += ReadDuration(micro, "delay_before");
            var op = ReadOp(micro);
            var duration = ReadDuration(micro, "duration");
            var intensity = (int)ReadNumber(micro, "intensity");
            _logger.LogInformation(
                "Mock PiShock {BackendId} step {Step}/{Total}: {Op} for {DurationMs}ms at {Intensity}% (offset +{OffsetMs}ms, sensation {SensationId})",
                Id, i + 1, request.Microsensations.Count, op,
                duration.TotalMilliseconds, intensity,
                offset.TotalMilliseconds, request.SensationId);
            offset += duration;
        }
    }

    private static PishockOp ReadOp(MicrosensationParameters micro)
    {
        if (micro.Values.TryGetValue("op", out var v) && v is ParameterValue.EnumValue e
            && System.Enum.TryParse<PishockOp>(e.Value, ignoreCase: true, out var op))
        {
            return op;
        }
        // Schema enforces required+enum; reaching here means a caller
        // bypassed the coordinator with a malformed request. Default to
        // Vibrate so the AllowedOps check below at least surfaces a
        // structured rejection.
        return PishockOp.Vibrate;
    }

    private static double ReadNumber(MicrosensationParameters micro, string key) =>
        micro.Values.TryGetValue(key, out var v) && v is ParameterValue.Number n ? n.Value : 0;

    private static TimeSpan ReadDuration(MicrosensationParameters micro, string key) =>
        micro.Values.TryGetValue(key, out var v) && v is ParameterValue.Duration d ? d.Value : TimeSpan.Zero;

    private static TimeSpan ComputeEstimatedDuration(BackendTriggerRequest request)
    {
        var total = TimeSpan.Zero;
        foreach (var micro in request.Microsensations)
        {
            total += ReadDuration(micro, "delay_before") + ReadDuration(micro, "duration");
        }
        return total;
    }

    private static IReadOnlyList<string> BuildCapabilities(IReadOnlyList<PishockOp> allowed)
    {
        var caps = new List<string> { "pishock" };
        if (allowed.Contains(PishockOp.Vibrate)) caps.Add("vibrate");
        if (allowed.Contains(PishockOp.Beep)) caps.Add("beep");
        if (allowed.Contains(PishockOp.Shock)) caps.Add("shock");
        caps.Add("ratelimited");
        return caps;
    }

    private static ZoneTopology BuildZones()
    {
        var topology = new ZoneTopology();
        topology.Zones.Add(new Zone
        {
            Id = "shock",
            DisplayName = "Shock zone",
            // Single-zone device — position is illustrative only; the
            // body map's BackendId+Region binding is what places the
            // shocker on the user's body, not this hint.
            Position = new PositionHint { X = 0.5f, Y = 0.5f, Z = 0.5f, Frame = "device" },
        });
        return topology;
    }

    private static ParameterSchema BuildParameters(IReadOnlyList<PishockOp> allowed)
    {
        var schema = new ParameterSchema();

        var opDef = new ParameterDef
        {
            Name = "op",
            Type = ParameterType.Enum,
            Required = true,
            Description = "PiShock operation type — Vibrate, Beep, or Shock",
        };
        // Only the descriptor's AllowedOps surface in the schema, so a
        // sensation file with op=Shock against a vibrate-only descriptor
        // gets rejected upstream by SensationValidator with a structured
        // INVALID_PARAMETER, no need to wait until trigger time.
        foreach (var allowedOp in allowed)
        {
            opDef.EnumValues.Add(allowedOp.ToString());
        }
        schema.Parameters.Add(opDef);

        schema.Parameters.Add(new ParameterDef
        {
            Name = "duration",
            Type = ParameterType.Duration,
            Required = true,
            Min = 0,
            // 15s is the manufacturer's UI ceiling; the per-descriptor
            // MaxDurationMs (default 1500ms) is a tighter daemon-level
            // cap enforced at trigger time.
            Max = 15,
            Description = "Per-op duration",
        });

        schema.Parameters.Add(new ParameterDef
        {
            Name = "intensity",
            Type = ParameterType.Number,
            Required = true,
            Min = 0,
            Max = 100,
            Unit = "%",
            Description = "Stimulation intensity (0..100)",
        });

        schema.Parameters.Add(new ParameterDef
        {
            Name = "delay_before",
            Type = ParameterType.Duration,
            Required = false,
            Min = 0,
            Max = 60,
            Description = "Quiet gap before this microsensation fires; for multi-pulse patterns",
        });

        return schema;
    }
}

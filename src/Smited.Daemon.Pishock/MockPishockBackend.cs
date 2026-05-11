using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Smited.Daemon.Backends;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.BodyMap;
using Smited.Daemon.Pishock.Internal;
using Smited.V1;

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
    private readonly PishockBackendOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<MockPishockBackend> _logger;
    private readonly Channel<BackendEvent> _events = Channel.CreateUnbounded<BackendEvent>();
    private readonly TokenBucket _bucket;
    /// <summary>
    /// Backend-lifetime CTS linked into every per-trigger CTS. Cancelling
    /// it on disposal aborts the in-flight playback's pending Task.Delay
    /// so the mock matches the real backend's shutdown semantic — no
    /// late events firing on a disposed backend.
    /// </summary>
    private readonly CancellationTokenSource _disposing = new();

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
        Capabilities = PishockDescriptors.BuildCapabilities(options.EffectiveAllowedOps);
        Zones = PishockDescriptors.BuildZones();
        Parameters = PishockDescriptors.BuildParameters(options.EffectiveAllowedOps);
        Concurrency = new ConcurrencyModel
        {
            MaxConcurrent = 1,
            // RejectNew rather than CancelOldest because PiShock's wire
            // protocol has no "cancel in-progress" message — preempting
            // would only cancel the daemon's local Task.Delay while the
            // device kept firing, then send a second op overlapping the
            // first.
            Policy = ConcurrencyPolicy.RejectNew,
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

    public IReadOnlySet<BodyRegion> ForbiddenRegions => PishockDescriptors.ManufacturerForbiddenRegions;

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
            PishockTriggerValidator.ValidateMicrosensation(
                i, request.Microsensations[i], request.IntensityScale, _options);
        }

        // Pre-allocate one bucket token per FIREABLE microsensation
        // atomically. Zero-duration microsensations are no-ops — they
        // don't fire on the device, so they don't consume bucket
        // budget. Atomic so a partial failure can't leak tokens for
        // the next trigger to inherit.
        var needed = CountFireable(request.Microsensations);
        if (needed > 0 && !_bucket.TryConsume(needed))
        {
            throw new BackendTriggerRejectedException(
                TriggerErrorCode.RateLimited,
                $"trigger needs {needed} bucket tokens; bump MaxBurst or slow "
                + "down the trigger rate",
                "rate_limit");
        }

        var estimated = MicrosensationReader.ComputeEstimatedDuration(request, _options.Mode);

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
        // the test's expected window. Linked to _disposing.Token so
        // backend disposal cancels the pending delay instead of
        // letting it fire on a disposed backend.
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposing.Token);
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

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _disposing.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }
        _events.Writer.TryComplete();
        _disposing.Dispose();
    }

    private void EmitEvent(BackendEvent evt) => _events.Writer.TryWrite(evt);

    private static int CountFireable(IReadOnlyList<MicrosensationParameters> micros)
    {
        var count = 0;
        foreach (var m in micros)
        {
            if (MicrosensationReader.ReadDuration(m, "duration") > TimeSpan.Zero)
            {
                count++;
            }
        }
        return count;
    }

    private void LogPulses(BackendTriggerRequest request)
    {
        var offset = TimeSpan.Zero;
        for (var i = 0; i < request.Microsensations.Count; i++)
        {
            var micro = request.Microsensations[i];
            offset += MicrosensationReader.ReadDuration(micro, "delay_before");
            var op = MicrosensationReader.ReadOp(micro);
            var duration = MicrosensationReader.ReadDuration(micro, "duration");
            var authoredIntensity = (int)MicrosensationReader.ReadNumber(micro, "intensity");
            var intensity = MicrosensationReader.ApplyIntensityScale(
                authoredIntensity, request.IntensityScale);
            _logger.LogInformation(
                "Mock PiShock {BackendId} step {Step}/{Total}: {Op} for {DurationMs}ms at {Intensity}% (offset +{OffsetMs}ms, sensation {SensationId})",
                Id, i + 1, request.Microsensations.Count, op,
                duration.TotalMilliseconds, intensity,
                offset.TotalMilliseconds, request.SensationId);
            offset += duration;
        }
    }
}

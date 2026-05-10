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
/// Real PiShock backend. Identical to <see cref="MockPishockBackend"/>
/// in shape — same descriptors, same validation, same rate limiter —
/// but its playback task calls <see cref="IPishockClient.SendOpAsync"/>
/// per microsensation instead of logging.
/// </summary>
/// <remarks>
/// <para>
/// Each instance is independent. The factory builds one client per
/// descriptor (cloud-mode descriptors share an HttpClient; LAN-mode
/// descriptors get their own per-IP). No static SDK, no shared state.
/// </para>
/// <para>
/// Sequence playback fires each microsensation sequentially: wait
/// <c>delay_before</c>, send the op, wait the op's <c>duration</c>,
/// move to the next. Op send is fire-and-forget per PiShock's wire
/// protocol; the daemon waits the authored duration before considering
/// the pulse "done" because the device offers no completion callback.
/// </para>
/// </remarks>
public sealed class PishockBackend : IHapticBackend
{
    private readonly PishockBackendOptions _options;
    private readonly IPishockClient _client;
    private readonly TimeProvider _time;
    private readonly ILogger<PishockBackend> _logger;
    private readonly Channel<BackendEvent> _events = Channel.CreateUnbounded<BackendEvent>();
    private readonly TokenBucket _bucket;
    /// <summary>
    /// Backend-lifetime CTS linked into every per-trigger CTS. Cancelling
    /// it on disposal aborts every in-flight playback's pending awaits so
    /// shutdown doesn't leak future client calls firing on a disposed
    /// backend.
    /// </summary>
    private readonly CancellationTokenSource _disposing = new();

    public PishockBackend(
        string id,
        PishockBackendOptions options,
        IPishockClient client,
        TimeProvider time,
        ILogger<PishockBackend> logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        Id = id;
        _options = options;
        _client = client;
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

    public CalibrationState? Calibration => null;

    public Struct? Extras => null;

    public IReadOnlySet<BodyRegion> ForbiddenRegions => PishockDescriptors.ManufacturerForbiddenRegions;

    public IAsyncEnumerable<BackendEvent> Events => _events.Reader.ReadAllAsync();

    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<BackendTriggerResult> TriggerAsync(
        BackendTriggerRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        for (var i = 0; i < request.Microsensations.Count; i++)
        {
            PishockTriggerValidator.ValidateMicrosensation(i, request.Microsensations[i], _options);
        }

        // Pre-allocate one token per FIREABLE microsensation atomically.
        // Zero-duration microsensations are no-ops (skipped during
        // playback); they don't consume bucket budget. A non-atomic
        // loop would also leak tokens on partial failure — see the
        // matching block in MockPishockBackend for the rationale.
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

        // Link to BOTH the caller's ct AND the backend's _disposing
        // token so disposal aborts every pending await across every
        // active trigger.
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposing.Token);
        _ = Task.Run(() => RunPlaybackAsync(request, linked));

        return Task.FromResult(new BackendTriggerResult(request.SensationId, estimated));
    }

    private async Task RunPlaybackAsync(
        BackendTriggerRequest request, CancellationTokenSource linked)
    {
        var ct = linked.Token;
        BackendEvent finalEvent;
        // Captures the device's rejection reason so the SensationCancelled
        // event can carry "why" through to event-stream and history
        // consumers.
        string? deviceRejectReason = null;
        try
        {
            for (var i = 0; i < request.Microsensations.Count; i++)
            {
                var micro = request.Microsensations[i];
                var delayBefore = MicrosensationReader.ReadDuration(micro, "delay_before");
                if (delayBefore > TimeSpan.Zero)
                {
                    await Task.Delay(delayBefore, _time, ct).ConfigureAwait(false);
                }

                var op = MicrosensationReader.ReadOp(micro);
                var duration = MicrosensationReader.ReadDuration(micro, "duration");

                // Zero authored duration is a no-op step: skip the
                // client call entirely. Cloud's whole-second rounding
                // would otherwise turn a silent microsensation into a
                // 1-second device fire (the cloud API's minimum). The
                // delay_before above already ran, so a delay-only
                // step is preserved.
                if (duration <= TimeSpan.Zero)
                {
                    continue;
                }

                var authoredIntensity = (int)MicrosensationReader.ReadNumber(micro, "intensity");
                // Apply the trigger's runtime IntensityScale; the
                // device sees the scaled value, not the authored one.
                var intensity = MicrosensationReader.ApplyIntensityScale(
                    authoredIntensity, request.IntensityScale);

                _logger.LogInformation(
                    "PiShock {BackendId} step {Step}/{Total}: firing {Op} for {DurationMs}ms at {Intensity}% (sensation {SensationId})",
                    Id, i + 1, request.Microsensations.Count,
                    op, duration.TotalMilliseconds, intensity, request.SensationId);

                var result = await _client.SendOpAsync(op, (int)duration.TotalMilliseconds, intensity, ct)
                    .ConfigureAwait(false);
                if (!result.Accepted)
                {
                    // Device or network said no. Bail out of the
                    // sequence — credentials don't become valid mid
                    // sequence, an offline device doesn't come back.
                    // The Cancelled event below carries the reason
                    // through to event-stream/history consumers so
                    // they don't see "Completed" for a sensation
                    // that didn't reach the hardware.
                    var reason = result.ErrorMessage ?? "(no message)";
                    _logger.LogWarning(
                        "PiShock {BackendId} step {Step}/{Total}: device rejected {Op}: {Error}",
                        Id, i + 1, request.Microsensations.Count, op, reason);
                    deviceRejectReason = $"device rejected: {reason}";
                    break;
                }

                // Wait the EFFECTIVE duration — what the device is
                // actually firing for over the wire. Cloud rounds
                // sub-second authored values up to whole seconds; LAN
                // passes milliseconds through. Using the authored
                // duration here would free the concurrency slot before
                // the cloud device finished firing, allowing overlap
                // on supposedly single-channel hardware.
                var effective = PishockDurationPolicy.Effective(_options.Mode, duration);
                if (effective > TimeSpan.Zero)
                {
                    await Task.Delay(effective, _time, ct).ConfigureAwait(false);
                }
            }

            finalEvent = deviceRejectReason is not null
                ? new SensationCancelled(
                    Id, _time.GetUtcNow(),
                    request.SensationId, request.SensationName, request.ClientTraceId,
                    Reason: deviceRejectReason)
                : new SensationCompleted(
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
    }

    /// <inheritdoc />
    /// <remarks>
    /// PiShock's wire protocol has no "cancel an in-progress op" message.
    /// The coordinator's CTS cancellation cuts the playback task's
    /// <c>Task.Delay</c>, freeing the concurrency slot, but the device
    /// continues firing the in-progress op until its authored duration
    /// elapses.
    /// </remarks>
    public Task<int> StopAsync(BackendStopRequest request, CancellationToken ct) =>
        Task.FromResult(0);

    public async ValueTask DisposeAsync()
    {
        try
        {
            // Cancel _disposing first so every active playback's linked
            // CTS fires; their pending Task.Delays throw and the
            // playback tasks exit through their catch path. Completing
            // the channel writer afterward lets late SensationCancelled
            // events flush to subscribers that are still reading.
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
}

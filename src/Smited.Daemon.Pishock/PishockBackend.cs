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

        // Pre-allocate one token per microsensation atomically. See the
        // matching block in MockPishockBackend for the rationale: a
        // non-atomic loop leaks tokens on partial failure and breaks
        // follow-up triggers that depended on those tokens still being
        // in the bucket.
        var needed = request.Microsensations.Count;
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

        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => RunPlaybackAsync(request, linked));

        return Task.FromResult(new BackendTriggerResult(request.SensationId, estimated));
    }

    private async Task RunPlaybackAsync(
        BackendTriggerRequest request, CancellationTokenSource linked)
    {
        var ct = linked.Token;
        BackendEvent finalEvent;
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
                var intensity = (int)MicrosensationReader.ReadNumber(micro, "intensity");

                _logger.LogInformation(
                    "PiShock {BackendId} step {Step}/{Total}: firing {Op} for {DurationMs}ms at {Intensity}% (sensation {SensationId})",
                    Id, i + 1, request.Microsensations.Count,
                    op, duration.TotalMilliseconds, intensity, request.SensationId);

                var result = await _client.SendOpAsync(op, (int)duration.TotalMilliseconds, intensity, ct)
                    .ConfigureAwait(false);
                if (!result.Accepted)
                {
                    // Device or network said no. The trigger has already
                    // returned Accepted to the coordinator; the failure
                    // surfaces here as a log line. A future event-stream
                    // refinement could emit a BackendError so the gRPC
                    // event subscribers see it too.
                    _logger.LogWarning(
                        "PiShock {BackendId} step {Step}/{Total}: device rejected {Op}: {Error}",
                        Id, i + 1, request.Microsensations.Count, op,
                        result.ErrorMessage ?? "(no message)");
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

    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private void EmitEvent(BackendEvent evt) => _events.Writer.TryWrite(evt);
}

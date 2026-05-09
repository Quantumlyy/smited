using System.Reflection;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Smited.Daemon.Backends;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Events;
using Smited.Daemon.History;
using Smited.Daemon.Sensations;
using Smited.Daemon.Triggering;
using Smited.V1;
using DomainEvent = Smited.Daemon.Backends.Internal.BackendEvent;
using ProtoEvent = Smited.V1.Event;

namespace Smited.Daemon.Services;

/// <summary>
/// gRPC entry point. Translates wire requests to domain operations via
/// <see cref="TriggerCoordinator"/>, <see cref="BackendRegistry"/>,
/// <see cref="SensationLibrary"/> and <see cref="EventStream"/>, then
/// translates outcomes back to the wire shape with <see cref="ProtoMappers"/>.
/// </summary>
internal sealed class SmitedGrpcService : SmitedService.SmitedServiceBase
{
    private static readonly string DaemonVersion =
        typeof(SmitedGrpcService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(SmitedGrpcService).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    private readonly BackendRegistry _registry;
    private readonly SensationLibrary _library;
    private readonly TriggerCoordinator _coordinator;
    private readonly EventStream _events;
    private readonly DaemonStartTime _startTime;
    private readonly IHistoryRecorder _history;
    private readonly TimeProvider _time;
    private readonly ILogger<SmitedGrpcService> _logger;

    public SmitedGrpcService(
        BackendRegistry registry,
        SensationLibrary library,
        TriggerCoordinator coordinator,
        EventStream events,
        DaemonStartTime startTime,
        IHistoryRecorder history,
        TimeProvider time,
        ILogger<SmitedGrpcService> logger)
    {
        _registry = registry;
        _library = library;
        _coordinator = coordinator;
        _events = events;
        _startTime = startTime;
        _history = history;
        _time = time;
        _logger = logger;
    }

    public override Task<HealthResponse> Health(HealthRequest request, ServerCallContext context)
    {
        var response = new HealthResponse
        {
            DaemonRunning = true,
            StartedAt = Timestamp.FromDateTimeOffset(_startTime.At),
            Version = DaemonVersion,
        };
        foreach (var backend in _registry.All)
        {
            response.Backends.Add(ProtoMappers.ToProtoSummary(backend));
        }
        return Task.FromResult(response);
    }

    public override Task<ListBackendsResponse> ListBackends(ListBackendsRequest request, ServerCallContext context)
    {
        var response = new ListBackendsResponse();
        foreach (var backend in _registry.All)
        {
            if (request.WithCapabilities.Count > 0 &&
                !request.WithCapabilities.All(cap =>
                    backend.Capabilities.Contains(cap, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }
            response.Backends.Add(ProtoMappers.ToProtoSummary(backend));
        }
        return Task.FromResult(response);
    }

    public override Task<DescribeBackendResponse> DescribeBackend(
        DescribeBackendRequest request, ServerCallContext context)
    {
        var backend = _registry.TryGet(request.BackendId)
            ?? throw new RpcException(new Status(StatusCode.NotFound,
                $"backend '{request.BackendId}' is not registered"));
        return Task.FromResult(ProtoMappers.ToDescribeResponse(backend));
    }

    public override async Task<TriggerResponse> Trigger(TriggerRequest request, ServerCallContext context)
    {
        var input = ProtoMappers.FromProtoTriggerRequest(request);
        var outcome = await _coordinator.TriggerAsync(input, context.CancellationToken)
            .ConfigureAwait(false);

        _ = _history.RecordTriggerAsync(BuildTriggerRecord(input, outcome));

        return ProtoMappers.ToProtoTriggerResponse(outcome);
    }

    public override async Task<StopResponse> Stop(StopRequest request, ServerCallContext context)
    {
        var record = new StopRecord
        {
            Timestamp = _time.GetUtcNow(),
            Source = "grpc",
            All = request.TargetCase == StopRequest.TargetOneofCase.All && request.All,
            SensationId = request.TargetCase == StopRequest.TargetOneofCase.SensationId
                ? request.SensationId : null,
            BackendId = request.TargetCase == StopRequest.TargetOneofCase.BackendId
                ? request.BackendId : null,
        };

        int stopped = request.TargetCase switch
        {
            StopRequest.TargetOneofCase.SensationId => await _coordinator.StopAsync(
                new BackendStopRequest(request.SensationId, All: false),
                context.CancellationToken),
            StopRequest.TargetOneofCase.BackendId => await _coordinator.StopBackendAsync(
                request.BackendId, context.CancellationToken),
            StopRequest.TargetOneofCase.All when request.All => await _coordinator.StopAsync(
                new BackendStopRequest(null, All: true),
                context.CancellationToken),
            _ => 0,
        };

        record.StoppedCount = stopped;
        _ = _history.RecordStopAsync(record);

        return new StopResponse { StoppedCount = (uint)stopped };
    }

    private TriggerRecord BuildTriggerRecord(ResolvedTriggerInput input, TriggerOutcome outcome)
    {
        var (sensationId, accepted, errorCode, errorField) = outcome switch
        {
            TriggerOutcome.Accepted a => (a.SensationId, true, (string?)null, (string?)null),
            TriggerOutcome.Rejected r => (string.Empty, false, r.Code.ToString(), r.Field),
            _ => (string.Empty, false, "Unknown", (string?)null),
        };
        return new TriggerRecord
        {
            Timestamp = _time.GetUtcNow(),
            BackendId = input.BackendId,
            SensationName = input.SensationName,
            SensationId = sensationId,
            ZoneIdsJson = JsonSerializer.Serialize(input.ZoneIds),
            IntensityScale = input.IntensityScale,
            Priority = input.Priority,
            ClientTraceId = input.ClientTraceId,
            Accepted = accepted,
            ErrorCode = errorCode,
            ErrorField = errorField,
        };
    }

    public override Task<ListSensationsResponse> ListSensations(
        ListSensationsRequest request, ServerCallContext context)
    {
        var entries = _library.List(
            string.IsNullOrEmpty(request.BackendId) ? null : request.BackendId,
            request.Tags.Count > 0 ? request.Tags.ToArray() : null);

        var response = new ListSensationsResponse();
        foreach (var entry in entries)
        {
            response.Sensations.Add(ProtoMappers.ToProtoRegistered(entry));
        }
        return Task.FromResult(response);
    }

    public override async Task<RegisterSensationResponse> RegisterSensation(
        RegisterSensationRequest request, ServerCallContext context)
    {
        var sensation = request.Sensation;
        var backend = _registry.TryGet(sensation.BackendId);
        if (backend is null)
        {
            return new RegisterSensationResponse
            {
                Registered = false,
                Error = $"backend '{sensation.BackendId}' is not registered",
            };
        }

        if (!backend.Capabilities.Contains("sensation_registry_mutable", StringComparer.OrdinalIgnoreCase))
        {
            return new RegisterSensationResponse
            {
                Registered = false,
                Error = $"backend '{backend.Id}' does not permit runtime sensation registration "
                    + "(missing 'sensation_registry_mutable' capability)",
            };
        }

        var domain = ProtoMappers.FromProtoRegistered(sensation);
        bool ok;
        try
        {
            ok = await _library.RegisterAsync(domain, backend.Kind, request.Overwrite, context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to persist sensation {Name} for backend {BackendId}",
                domain.Name, domain.BackendId);
            return new RegisterSensationResponse
            {
                Registered = false,
                Error = $"failed to persist sensation: {ex.Message}",
            };
        }

        if (!ok)
        {
            return new RegisterSensationResponse
            {
                Registered = false,
                Error = $"sensation '{domain.Name}' already exists for backend '{domain.BackendId}' "
                    + "(set overwrite=true to replace)",
            };
        }
        return new RegisterSensationResponse { Registered = true };
    }

    public override async Task<UnregisterSensationResponse> UnregisterSensation(
        UnregisterSensationRequest request, ServerCallContext context)
    {
        var backend = _registry.TryGet(request.BackendId);
        if (backend is null)
        {
            return new UnregisterSensationResponse { Unregistered = false };
        }

        if (!backend.Capabilities.Contains("sensation_registry_mutable", StringComparer.OrdinalIgnoreCase))
        {
            return new UnregisterSensationResponse { Unregistered = false };
        }

        var removed = await _library.UnregisterAsync(
                request.BackendId, backend.Kind, request.Name, context.CancellationToken)
            .ConfigureAwait(false);
        return new UnregisterSensationResponse { Unregistered = removed };
    }

    public override async Task SubscribeEvents(
        SubscribeEventsRequest request,
        IServerStreamWriter<ProtoEvent> responseStream,
        ServerCallContext context)
    {
        var filters = new SubscribeFilters(
            new HashSet<EventKind>(request.Kinds),
            new HashSet<string>(request.BackendIds, StringComparer.OrdinalIgnoreCase));

        var peer = context.Peer ?? "unknown";
        await foreach (var evt in _events.StreamAsync(filters, peer, context.CancellationToken)
                           .ConfigureAwait(false))
        {
            await responseStream.WriteAsync(ProtoMappers.ToProtoEvent(evt), context.CancellationToken)
                .ConfigureAwait(false);
        }
    }
}

/// <summary>Captures the daemon's start timestamp for the Health response.</summary>
internal sealed class DaemonStartTime
{
    public DaemonStartTime(TimeProvider time)
    {
        At = time.GetUtcNow();
    }

    public DateTimeOffset At { get; }
}

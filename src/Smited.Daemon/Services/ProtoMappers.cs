using Google.Protobuf.WellKnownTypes;
using Smited.Daemon.Backends;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Triggering;
using Smited.V1;
using DomainParameterValue = Smited.Daemon.Backends.Internal.ParameterValue;
using DomainRegistered = Smited.Daemon.Sensations.RegisteredSensation;
using DomainMicrosensationParameters = Smited.Daemon.Backends.Internal.MicrosensationParameters;
using ProtoParameterValue = Smited.V1.ParameterValue;
using ProtoRegistered = Smited.V1.RegisteredSensation;
using ProtoEvent = Smited.V1.Event;

namespace Smited.Daemon.Services;

/// <summary>
/// Translation between internal domain records and the generated wire types.
/// All proto&lt;-&gt;domain conversions live here so the gRPC handlers stay
/// thin and the domain model stays free of generated-type imports beyond
/// the static descriptor types it already exposes.
/// </summary>
internal static class ProtoMappers
{
    public static BackendSummary ToProtoSummary(IHapticBackend backend)
    {
        var summary = new BackendSummary
        {
            Id = backend.Id,
            Kind = backend.Kind,
            DisplayName = backend.DisplayName,
            Status = backend.Status,
        };
        foreach (var capability in backend.Capabilities)
        {
            summary.Capabilities.Add(capability);
        }
        return summary;
    }

    public static BackendSummary ToProtoSummary(BackendSummarySnapshot snapshot)
    {
        var summary = new BackendSummary
        {
            Id = snapshot.Id,
            Kind = snapshot.Kind,
            DisplayName = snapshot.DisplayName,
            Status = snapshot.Status,
        };
        foreach (var capability in snapshot.Capabilities)
        {
            summary.Capabilities.Add(capability);
        }
        return summary;
    }

    public static DescribeBackendResponse ToDescribeResponse(IHapticBackend backend)
    {
        var response = new DescribeBackendResponse
        {
            Summary = ToProtoSummary(backend),
            Zones = backend.Zones,
            Parameters = backend.Parameters,
            Concurrency = backend.Concurrency,
        };
        if (backend.Calibration is not null)
        {
            response.Calibration = backend.Calibration;
        }
        if (backend.Extras is not null)
        {
            response.Extras = backend.Extras;
        }
        return response;
    }

    public static TriggerResponse ToProtoTriggerResponse(TriggerOutcome outcome) => outcome switch
    {
        TriggerOutcome.Accepted a => new TriggerResponse
        {
            Accepted = true,
            SensationId = a.SensationId,
            ClientTraceId = a.ClientTraceId,
        },
        TriggerOutcome.Rejected r => new TriggerResponse
        {
            Accepted = false,
            ClientTraceId = r.ClientTraceId,
            Error = new TriggerError
            {
                Code = r.Code,
                Message = r.Message,
                Field = r.Field ?? string.Empty,
            },
        },
        _ => throw new InvalidOperationException($"Unknown TriggerOutcome variant: {outcome.GetType().Name}"),
    };

    public static ResolvedTriggerInput FromProtoTriggerRequest(TriggerRequest request)
    {
        IReadOnlyList<DomainMicrosensationParameters>? inline = null;
        string? sensationName = null;

        switch (request.SensationCase)
        {
            case TriggerRequest.SensationOneofCase.SensationName:
                sensationName = request.SensationName;
                break;
            case TriggerRequest.SensationOneofCase.Inline:
                inline = request.Inline.Microsensations
                    .Select(m =>
                    {
                        var dict = new Dictionary<string, DomainParameterValue>(StringComparer.OrdinalIgnoreCase);
                        foreach (var kv in m.Parameters)
                        {
                            dict[kv.Key] = ToInternalParameterValue(kv.Value);
                        }
                        return new DomainMicrosensationParameters(dict);
                    })
                    .ToArray();
                break;
        }

        return new ResolvedTriggerInput(
            BackendId: request.BackendId,
            SensationName: sensationName,
            InlineMicrosensations: inline,
            ZoneIds: request.ZoneIds.ToArray(),
            IntensityScale: request.HasIntensityScale ? request.IntensityScale : null,
            Priority: request.Priority,
            ClientTraceId: request.ClientTraceId);
    }

    public static DomainParameterValue ToInternalParameterValue(ProtoParameterValue value) =>
        value.ValueCase switch
        {
            ProtoParameterValue.ValueOneofCase.Number => new DomainParameterValue.Number(value.Number),
            ProtoParameterValue.ValueOneofCase.BoolValue => new DomainParameterValue.Bool(value.BoolValue),
            ProtoParameterValue.ValueOneofCase.StringValue => new DomainParameterValue.Text(value.StringValue),
            ProtoParameterValue.ValueOneofCase.Duration => new DomainParameterValue.Duration(value.Duration.ToTimeSpan()),
            ProtoParameterValue.ValueOneofCase.EnumValue => new DomainParameterValue.EnumValue(value.EnumValue),
            _ => throw new InvalidOperationException($"ParameterValue oneof not set: {value.ValueCase}"),
        };

    public static ProtoParameterValue ToProtoParameterValue(DomainParameterValue value) => value switch
    {
        DomainParameterValue.Number n => new ProtoParameterValue { Number = n.Value },
        DomainParameterValue.Bool b => new ProtoParameterValue { BoolValue = b.Value },
        DomainParameterValue.Text t => new ProtoParameterValue { StringValue = t.Value },
        DomainParameterValue.Duration d => new ProtoParameterValue
        {
            Duration = Duration.FromTimeSpan(d.Value),
        },
        DomainParameterValue.EnumValue e => new ProtoParameterValue { EnumValue = e.Value },
        _ => throw new InvalidOperationException($"Unknown ParameterValue variant: {value.GetType().Name}"),
    };

    public static ProtoRegistered ToProtoRegistered(DomainRegistered s)
    {
        var inline = new InlineSensation();
        foreach (var micro in s.Definition)
        {
            var m = new Microsensation();
            foreach (var (key, value) in micro.Values)
            {
                m.Parameters[key] = ToProtoParameterValue(value);
            }
            inline.Microsensations.Add(m);
        }

        var registered = new ProtoRegistered
        {
            Name = s.Name,
            BackendId = s.BackendId,
            DisplayName = s.DisplayName,
            Description = s.Description,
            EstimatedDuration = Duration.FromTimeSpan(s.EstimatedDuration),
            RegisteredAt = Timestamp.FromDateTimeOffset(s.RegisteredAt),
            Definition = inline,
        };
        if (s.DefaultIntensity is { } intensity)
        {
            registered.DefaultIntensity = intensity;
        }
        foreach (var tag in s.Tags) registered.Tags.Add(tag);
        foreach (var zone in s.DefaultZoneIds) registered.DefaultZoneIds.Add(zone);
        return registered;
    }

    public static DomainRegistered FromProtoRegistered(ProtoRegistered r)
    {
        var definition = r.Definition.Microsensations
            .Select(m =>
            {
                var dict = new Dictionary<string, DomainParameterValue>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in m.Parameters)
                {
                    dict[kv.Key] = ToInternalParameterValue(kv.Value);
                }
                return new DomainMicrosensationParameters(dict);
            })
            .ToArray();

        return new DomainRegistered(
            Name: r.Name,
            BackendId: r.BackendId,
            DisplayName: r.DisplayName,
            Description: r.Description,
            Tags: r.Tags.ToArray(),
            DefaultZoneIds: r.DefaultZoneIds.ToArray(),
            DefaultIntensity: r.HasDefaultIntensity ? r.DefaultIntensity : null,
            EstimatedDuration: r.EstimatedDuration?.ToTimeSpan() ?? TimeSpan.Zero,
            RegisteredAt: r.RegisteredAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow,
            Definition: definition);
    }

    public static ProtoEvent ToProtoEvent(BackendEvent evt)
    {
        var protoEvent = new ProtoEvent
        {
            Timestamp = Timestamp.FromDateTimeOffset(evt.Timestamp),
            BackendId = evt.BackendId,
        };

        switch (evt)
        {
            case SensationStarted s:
                protoEvent.Kind = EventKind.SensationStarted;
                protoEvent.Sensation = MakeSensationLifecycle(s.SensationId, s.SensationName, s.ClientTraceId, reason: null);
                break;
            case SensationCompleted s:
                protoEvent.Kind = EventKind.SensationCompleted;
                protoEvent.Sensation = MakeSensationLifecycle(s.SensationId, s.SensationName, s.ClientTraceId, reason: null);
                break;
            case SensationCancelled s:
                protoEvent.Kind = EventKind.SensationCancelled;
                protoEvent.Sensation = MakeSensationLifecycle(s.SensationId, s.SensationName, s.ClientTraceId, s.Reason);
                break;
            case BackendLifecycleEvent b:
                protoEvent.Kind = b.Change switch
                {
                    BackendLifecycleChange.Registered => EventKind.BackendRegistered,
                    BackendLifecycleChange.Deregistered => EventKind.BackendDeregistered,
                    BackendLifecycleChange.StatusChanged => EventKind.BackendStatusChanged,
                    _ => EventKind.Unspecified,
                };
                var lifecycle = new BackendLifecycle { Summary = ToProtoSummary(b.Snapshot) };
                if (b.Reason is not null) lifecycle.Reason = b.Reason;
                protoEvent.Backend = lifecycle;
                break;
            case CalibrationChangedEvent c:
                protoEvent.Kind = EventKind.CalibrationChanged;
                protoEvent.Calibration = new CalibrationChanged { NewState = c.NewState };
                break;
            case SensationRegistryChangedEvent r:
                protoEvent.Kind = r.Change == SensationRegistryChange.Registered
                    ? EventKind.SensationRegistered
                    : EventKind.SensationUnregistered;
                protoEvent.Registry = new SensationRegistryChanged { Name = r.SensationName };
                break;
            default:
                throw new InvalidOperationException($"Unknown BackendEvent variant: {evt.GetType().Name}");
        }

        return protoEvent;
    }

    private static SensationLifecycle MakeSensationLifecycle(
        string sensationId, string? sensationName, string clientTraceId, string? reason)
    {
        var lifecycle = new SensationLifecycle
        {
            SensationId = sensationId,
            SensationName = sensationName ?? string.Empty,
            ClientTraceId = clientTraceId,
        };
        if (reason is not null)
        {
            lifecycle.Reason = reason;
        }
        return lifecycle;
    }
}

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Smited.Daemon.Backends;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.BodyMap;
using Smited.Daemon.Configuration;
using Smited.Daemon.Sensations;
using Smited.Daemon.Tests.Fixtures;
using Smited.Daemon.Triggering;
using Smited.V1;
using Xunit;
using ParameterValue = Smited.Daemon.Backends.Internal.ParameterValue;
using MicrosensationParameters = Smited.Daemon.Backends.Internal.MicrosensationParameters;
using RegisteredSensation = Smited.Daemon.Sensations.RegisteredSensation;

namespace Smited.Daemon.Tests.Triggering;

public class TriggerCoordinatorTests
{
    [Fact]
    public async Task Empty_backend_id_is_rejected_as_BACKEND_NOT_FOUND()
    {
        var sys = BuildSystem();

        var result = await sys.Coordinator.TriggerAsync(
            MakeInline("", "trace-1"),
            CancellationToken.None);

        var rejection = result.Should().BeOfType<TriggerOutcome.Rejected>().Subject;
        rejection.Code.Should().Be(TriggerErrorCode.BackendNotFound);
        rejection.ClientTraceId.Should().Be("trace-1");
    }

    [Fact]
    public async Task Unknown_backend_id_is_rejected_as_BACKEND_NOT_FOUND()
    {
        var sys = BuildSystem();

        var result = await sys.Coordinator.TriggerAsync(
            MakeInline("nonexistent", "trace-2"),
            CancellationToken.None);

        result.Should().BeOfType<TriggerOutcome.Rejected>()
            .Which.Code.Should().Be(TriggerErrorCode.BackendNotFound);
    }

    [Fact]
    public async Task Unknown_sensation_name_returns_SENSATION_NOT_FOUND_with_trace_echoed()
    {
        var sys = BuildSystem();

        var input = new ResolvedTriggerInput(
            BackendId: "mock",
            SensationName: "missing",
            InlineMicrosensations: null,
            ZoneIds: ["pectoral_l"],
            IntensityScale: null,
            Priority: 0,
            ClientTraceId: "trace-3");

        var result = await sys.Coordinator.TriggerAsync(input, CancellationToken.None);

        var rejection = result.Should().BeOfType<TriggerOutcome.Rejected>().Subject;
        rejection.Code.Should().Be(TriggerErrorCode.SensationNotFound);
        rejection.Field.Should().Be("sensation_name");
        rejection.ClientTraceId.Should().Be("trace-3");
    }

    [Fact]
    public async Task Unknown_zone_id_returns_INVALID_ZONE_with_field()
    {
        var sys = BuildSystem();

        var result = await sys.Coordinator.TriggerAsync(
            MakeInline("mock", "trace-4", zones: ["nonexistent"]),
            CancellationToken.None);

        var rejection = result.Should().BeOfType<TriggerOutcome.Rejected>().Subject;
        rejection.Code.Should().Be(TriggerErrorCode.InvalidZone);
        rejection.Field.Should().Be("zone_ids");
    }

    [Fact]
    public async Task Wrong_parameter_type_returns_INVALID_PARAMETER_with_path()
    {
        var sys = BuildSystem();

        var input = new ResolvedTriggerInput(
            BackendId: "mock",
            SensationName: null,
            InlineMicrosensations: new[]
            {
                new MicrosensationParameters(new Dictionary<string, ParameterValue>
                {
                    ["frequency"] = new ParameterValue.Duration(TimeSpan.FromSeconds(1)),
                    ["intensity"] = new ParameterValue.Number(50),
                }),
            },
            ZoneIds: ["pectoral_l"],
            IntensityScale: null,
            Priority: 0,
            ClientTraceId: "trace-5");

        var result = await sys.Coordinator.TriggerAsync(input, CancellationToken.None);

        var rejection = result.Should().BeOfType<TriggerOutcome.Rejected>().Subject;
        rejection.Code.Should().Be(TriggerErrorCode.InvalidParameter);
        rejection.Field.Should().Be("microsensations[0].parameters.frequency");
    }

    [Fact]
    public async Task Missing_required_parameter_returns_INVALID_PARAMETER()
    {
        var sys = BuildSystem();

        var result = await sys.Coordinator.TriggerAsync(
            MakeInline("mock", "trace-6", omitIntensity: true),
            CancellationToken.None);

        result.Should().BeOfType<TriggerOutcome.Rejected>()
            .Which.Code.Should().Be(TriggerErrorCode.InvalidParameter);
    }

    [Fact]
    public async Task Successful_trigger_returns_Accepted_with_sensation_id_and_trace_echoed()
    {
        var sys = BuildSystem();

        var result = await sys.Coordinator.TriggerAsync(
            MakeInline("mock", "trace-7"),
            CancellationToken.None);

        var accepted = result.Should().BeOfType<TriggerOutcome.Accepted>().Subject;
        accepted.ClientTraceId.Should().Be("trace-7");
        accepted.SensationId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Named_sensation_uses_library_default_zones_when_request_omits_zones()
    {
        var sys = BuildSystem();
        sys.Library.Register(new RegisteredSensation(
            Name: "ping",
            BackendId: "mock",
            DisplayName: "Ping",
            Description: "",
            Tags: Array.Empty<string>(),
            DefaultZoneIds: ["pectoral_r"],
            DefaultIntensity: 60,
            EstimatedDuration: TimeSpan.Zero,
            RegisteredAt: DateTimeOffset.UtcNow,
            Definition: new[]
            {
                new MicrosensationParameters(new Dictionary<string, ParameterValue>
                {
                    ["frequency"] = new ParameterValue.Number(50),
                    ["intensity"] = new ParameterValue.Number(50),
                }),
            }), overwrite: false);

        BackendTriggerRequest? captured = null;
        sys.Backend.OnTrigger = (req, _) =>
        {
            captured = req;
            return Task.FromResult(new BackendTriggerResult(req.SensationId, TimeSpan.Zero));
        };

        var result = await sys.Coordinator.TriggerAsync(
            new ResolvedTriggerInput(
                BackendId: "mock",
                SensationName: "ping",
                InlineMicrosensations: null,
                ZoneIds: Array.Empty<string>(),
                IntensityScale: null,
                Priority: 0,
                ClientTraceId: "trace-8"),
            CancellationToken.None);

        result.Should().BeOfType<TriggerOutcome.Accepted>();
        captured!.ZoneIds.Should().BeEquivalentTo("pectoral_r");
        captured.IntensityScale.Should().Be(60);
    }

    [Fact]
    public async Task Inline_intensity_scale_overrides_default()
    {
        var sys = BuildSystem();
        sys.Library.Register(new RegisteredSensation(
            Name: "ping",
            BackendId: "mock",
            DisplayName: "Ping",
            Description: "",
            Tags: Array.Empty<string>(),
            DefaultZoneIds: ["pectoral_l"],
            DefaultIntensity: 50,
            EstimatedDuration: TimeSpan.Zero,
            RegisteredAt: DateTimeOffset.UtcNow,
            Definition: new[]
            {
                new MicrosensationParameters(new Dictionary<string, ParameterValue>
                {
                    ["frequency"] = new ParameterValue.Number(50),
                    ["intensity"] = new ParameterValue.Number(50),
                }),
            }), overwrite: false);

        BackendTriggerRequest? captured = null;
        sys.Backend.OnTrigger = (req, _) =>
        {
            captured = req;
            return Task.FromResult(new BackendTriggerResult(req.SensationId, TimeSpan.Zero));
        };

        await sys.Coordinator.TriggerAsync(
            new ResolvedTriggerInput(
                BackendId: "mock",
                SensationName: "ping",
                InlineMicrosensations: null,
                ZoneIds: Array.Empty<string>(),
                IntensityScale: 25,
                Priority: 0,
                ClientTraceId: "trace-9"),
            CancellationToken.None);

        captured!.IntensityScale.Should().Be(25);
    }

    private static System BuildSystem(
        ConcurrencyPolicy policy = ConcurrencyPolicy.RejectNew,
        uint maxConcurrent = 1)
    {
        var sink = new RecordingEventSink();
        var time = new FakeTimeProvider();
        var registry = new BackendRegistry(sink, time);
        var library = new SensationLibrary(sink, time, Options.Create(new SmitedOptions()));
        var concurrency = new ConcurrencyEnforcer();

        var schema = new ParameterSchema();
        schema.Parameters.Add(new ParameterDef
        {
            Name = "frequency",
            Type = ParameterType.Number,
            Required = true,
            Min = 1,
            Max = 100,
            Description = "",
        });
        schema.Parameters.Add(new ParameterDef
        {
            Name = "intensity",
            Type = ParameterType.Number,
            Required = true,
            Min = 0,
            Max = 100,
            Description = "",
        });

        var topology = new ZoneTopology();
        topology.Zones.Add(new Zone { Id = "pectoral_l", DisplayName = "L" });
        topology.Zones.Add(new Zone { Id = "pectoral_r", DisplayName = "R" });

        var backend = new FakeBackend("mock", kind: "mock", capabilities: ["ems"])
        {
            Parameters = schema,
            Zones = topology,
            Concurrency = new ConcurrencyModel { MaxConcurrent = maxConcurrent, Policy = policy },
        };
        registry.Register(backend);

        var coordinator = new TriggerCoordinator(
            registry,
            library,
            concurrency,
            new BodyMapState(),
            time,
            NullLogger<TriggerCoordinator>.Instance);

        return new System(coordinator, backend, library, sink, time, concurrency);
    }

    private static ResolvedTriggerInput MakeInline(
        string backendId,
        string traceId,
        IReadOnlyList<string>? zones = null,
        bool omitIntensity = false)
    {
        var values = new Dictionary<string, ParameterValue>
        {
            ["frequency"] = new ParameterValue.Number(50),
        };
        if (!omitIntensity)
        {
            values["intensity"] = new ParameterValue.Number(50);
        }

        return new ResolvedTriggerInput(
            BackendId: backendId,
            SensationName: null,
            InlineMicrosensations: new[] { new MicrosensationParameters(values) },
            ZoneIds: zones ?? new[] { "pectoral_l" },
            IntensityScale: null,
            Priority: 0,
            ClientTraceId: traceId);
    }

    private sealed record System(
        TriggerCoordinator Coordinator,
        FakeBackend Backend,
        SensationLibrary Library,
        RecordingEventSink Sink,
        FakeTimeProvider Time,
        ConcurrencyEnforcer Concurrency);
}

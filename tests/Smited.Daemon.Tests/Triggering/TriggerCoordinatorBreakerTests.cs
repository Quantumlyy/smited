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

namespace Smited.Daemon.Tests.Triggering;

/// <summary>
/// Verifies the TriggerCoordinator's circuit-breaker integration: when
/// the breaker is tripped every Trigger rejects with
/// <see cref="TriggerErrorCode.BackendUnavailable"/> + a
/// <c>BREAKER_TRIPPED:</c> message prefix; once re-armed, triggers
/// succeed again.
/// </summary>
public class TriggerCoordinatorBreakerTests
{
    [Fact]
    public async Task Trigger_rejected_when_breaker_tripped()
    {
        var sys = BuildSystem();
        sys.Breaker.Trip("test");

        var result = await sys.Coordinator.TriggerAsync(
            MakeInline("mock", "trace-tripped"),
            CancellationToken.None);

        var rejection = result.Should().BeOfType<TriggerOutcome.Rejected>().Subject;
        rejection.Code.Should().Be(TriggerErrorCode.BackendUnavailable);
        rejection.Message.Should().StartWith("BREAKER_TRIPPED:");
        rejection.ClientTraceId.Should().Be("trace-tripped");
    }

    [Fact]
    public async Task Trigger_succeeds_after_breaker_rearmed()
    {
        var sys = BuildSystem();
        sys.Breaker.Trip("test");
        sys.Breaker.Rearm();

        var result = await sys.Coordinator.TriggerAsync(
            MakeInline("mock", "trace-rearmed"),
            CancellationToken.None);

        result.Should().BeOfType<TriggerOutcome.Accepted>();
    }

    [Fact]
    public async Task Breaker_check_runs_before_BackendId_validation()
    {
        // A tripped breaker rejects even the most trivially-malformed
        // input (empty backend_id). Confirms the breaker gate is at the
        // top of the validation pipeline rather than after backend lookup.
        var sys = BuildSystem();
        sys.Breaker.Trip("test");

        var result = await sys.Coordinator.TriggerAsync(
            MakeInline("", "trace-empty-backend"),
            CancellationToken.None);

        var rejection = result.Should().BeOfType<TriggerOutcome.Rejected>().Subject;
        rejection.Code.Should().Be(TriggerErrorCode.BackendUnavailable);
        rejection.Message.Should().StartWith("BREAKER_TRIPPED:");
    }

    private static System BuildSystem()
    {
        var sink = new RecordingEventSink();
        var time = new FakeTimeProvider();
        var registry = new BackendRegistry(sink, time);
        var library = new SensationLibrary(sink, time, Options.Create(new SmitedOptions()));
        var concurrency = new ConcurrencyEnforcer();
        var breaker = new BreakerService(time);

        var schema = new ParameterSchema();
        schema.Parameters.Add(new ParameterDef
        {
            Name = "frequency", Type = ParameterType.Number, Required = true,
            Min = 1, Max = 100, Description = "",
        });
        schema.Parameters.Add(new ParameterDef
        {
            Name = "intensity", Type = ParameterType.Number, Required = true,
            Min = 0, Max = 100, Description = "",
        });

        var topology = new ZoneTopology();
        topology.Zones.Add(new Zone { Id = "pectoral_l", DisplayName = "L" });

        var backend = new FakeBackend("mock", kind: "mock", capabilities: ["ems"])
        {
            Parameters = schema,
            Zones = topology,
            Concurrency = new ConcurrencyModel { MaxConcurrent = 1, Policy = ConcurrencyPolicy.RejectNew },
        };
        registry.Register(backend);

        var coordinator = new TriggerCoordinator(
            registry, library, concurrency, new BodyMapState(), breaker, time,
            NullLogger<TriggerCoordinator>.Instance);

        return new System(coordinator, breaker);
    }

    private static ResolvedTriggerInput MakeInline(string backendId, string traceId) =>
        new(
            BackendId: backendId,
            SensationName: null,
            InlineMicrosensations: new[]
            {
                new MicrosensationParameters(new Dictionary<string, ParameterValue>
                {
                    ["frequency"] = new ParameterValue.Number(50),
                    ["intensity"] = new ParameterValue.Number(50),
                }),
            },
            ZoneIds: new[] { "pectoral_l" },
            IntensityScale: null,
            Priority: 0,
            ClientTraceId: traceId);

    private sealed record System(TriggerCoordinator Coordinator, BreakerService Breaker);
}

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

    [Fact]
    public async Task Trigger_rejected_by_second_breaker_check_when_breaker_trips_mid_validation()
    {
        // Race regression: the top-of-method breaker gate isn't enough
        // because validation does an `await` on ConcurrencyEnforcer
        // (and on body-map checks before that). A panic that fires
        // *during* validation tripped the breaker but didn't stop the
        // already-mid-flight trigger from reaching the backend, where
        // it could race with the panic's StopAsync snapshot. The
        // coordinator's second breaker check just before
        // backend.TriggerAsync closes that window.
        //
        // We simulate the race by injecting an IBodyMapState whose
        // CheckOverlap call trips the breaker as a side effect — same
        // observable effect as a panic firing concurrently, but
        // deterministic.
        var triggerCount = 0;
        var sys = BuildSystemWithBreakerTrippingBodyMap(
            onBackendTrigger: (req, _) =>
            {
                Interlocked.Increment(ref triggerCount);
                return Task.FromResult(new BackendTriggerResult(req.SensationId, TimeSpan.Zero));
            });

        var result = await sys.Coordinator.TriggerAsync(
            MakeInline("mock", "trace-mid-flight"),
            CancellationToken.None);

        var rejection = result.Should().BeOfType<TriggerOutcome.Rejected>().Subject;
        rejection.Code.Should().Be(TriggerErrorCode.BackendUnavailable);
        rejection.Message.Should().StartWith("BREAKER_TRIPPED:");
        triggerCount.Should().Be(0,
            "the second breaker check should reject before the trigger reaches the backend");
    }

    private static System BuildSystem()
    {
        return BuildSystemCore(bodyMap: null, onBackendTrigger: null);
    }

    private static System BuildSystemWithBreakerTrippingBodyMap(
        Func<BackendTriggerRequest, CancellationToken, Task<BackendTriggerResult>>? onBackendTrigger)
    {
        // The body-map stub trips the breaker on its first CheckOverlap
        // call. CheckOverlap only runs when OverlapPolicy=Refuse, which
        // the stub also reports. The second breaker check inside
        // TriggerCoordinator.TriggerAsync runs AFTER body-map and
        // ConcurrencyEnforcer, so by the time the coordinator gets
        // there the breaker is tripped — matching the panic-mid-flight
        // race we want to defend against.
        return BuildSystemCore(
            bodyMap: breaker => new BreakerTrippingBodyMapState(breaker),
            onBackendTrigger: onBackendTrigger);
    }

    private static System BuildSystemCore(
        Func<IBreakerService, IBodyMapState>? bodyMap,
        Func<BackendTriggerRequest, CancellationToken, Task<BackendTriggerResult>>? onBackendTrigger)
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
            OnTrigger = onBackendTrigger,
        };
        registry.Register(backend);

        IBodyMapState bodyMapState = bodyMap is not null
            ? bodyMap(breaker)
            : new BodyMapState();

        var coordinator = new TriggerCoordinator(
            registry, library, concurrency, bodyMapState, breaker, time,
            NullLogger<TriggerCoordinator>.Instance);

        return new System(coordinator, breaker);
    }

    /// <summary>
    /// IBodyMapState stub that trips the breaker as a side effect of
    /// CheckOverlap. Lets the test reproduce a panic firing between
    /// the coordinator's top-of-method breaker gate and the backend
    /// dispatch — the race that motivated the second breaker check.
    /// </summary>
    private sealed class BreakerTrippingBodyMapState : IBodyMapState
    {
        private readonly IBreakerService _breaker;

        public BreakerTrippingBodyMapState(IBreakerService breaker) => _breaker = breaker;

        public OverlapPolicy OverlapPolicy => OverlapPolicy.Refuse;
        public int PlacementCount => 0;
        public int WarningCount => 0;

        public OverlapHit? CheckOverlap(IHapticBackend backend, IReadOnlyList<string> zoneIds)
        {
            _breaker.Trip("simulated mid-flight panic");
            return null;
        }
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

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

public class TriggerCoordinatorOverlapTests
{
    [Fact]
    public async Task Off_policy_does_not_reject_even_when_an_overlap_exists()
    {
        var sys = BuildSystem(new StubBodyMap(OverlapPolicy.Off, alwaysOverlapWith: "other"));

        var result = await sys.Coordinator.TriggerAsync(MakeInput("vest"), CancellationToken.None);

        result.Should().BeOfType<TriggerOutcome.Accepted>();
    }

    [Fact]
    public async Task Warn_policy_does_not_reject_at_trigger_time()
    {
        var sys = BuildSystem(new StubBodyMap(OverlapPolicy.Warn, alwaysOverlapWith: "other"));

        var result = await sys.Coordinator.TriggerAsync(MakeInput("vest"), CancellationToken.None);

        result.Should().BeOfType<TriggerOutcome.Accepted>();
    }

    [Fact]
    public async Task Refuse_policy_rejects_with_invalid_zone_when_overlap_present()
    {
        var sys = BuildSystem(new StubBodyMap(OverlapPolicy.Refuse, alwaysOverlapWith: "other"));

        var result = await sys.Coordinator.TriggerAsync(MakeInput("vest"), CancellationToken.None);

        var rejection = result.Should().BeOfType<TriggerOutcome.Rejected>().Subject;
        rejection.Code.Should().Be(TriggerErrorCode.InvalidZone);
        rejection.Message.Should().Contain("'other'");
        rejection.Field.Should().Be("zone_ids");
    }

    [Fact]
    public async Task Refuse_policy_does_not_reject_when_no_overlap()
    {
        var sys = BuildSystem(new StubBodyMap(OverlapPolicy.Refuse, alwaysOverlapWith: null));

        var result = await sys.Coordinator.TriggerAsync(MakeInput("vest"), CancellationToken.None);

        result.Should().BeOfType<TriggerOutcome.Accepted>();
    }

    private static System BuildSystem(IBodyMapState bodyMap)
    {
        var sink = new RecordingEventSink();
        var time = new FakeTimeProvider();
        var registry = new BackendRegistry(sink, time);
        var library = new SensationLibrary(sink, time, Options.Create(new SmitedOptions()));
        var concurrency = new ConcurrencyEnforcer();

        var schema = new ParameterSchema();
        schema.Parameters.Add(new ParameterDef
        {
            Name = "frequency", Type = ParameterType.Number, Required = true,
            Min = 1, Max = 100, Description = "",
        });
        var topology = new ZoneTopology();
        topology.Zones.Add(new Zone { Id = "z", DisplayName = "Z" });
        var backend = new FakeBackend("vest")
        {
            Parameters = schema,
            Zones = topology,
        };
        registry.Register(backend);

        var coordinator = new TriggerCoordinator(
            registry, library, concurrency, bodyMap, new BreakerService(time), time,
            NullLogger<TriggerCoordinator>.Instance);

        return new System(coordinator);
    }

    private static ResolvedTriggerInput MakeInput(string backendId) =>
        new(
            BackendId: backendId,
            SensationName: null,
            ClientTraceId: "trace",
            ZoneIds: ["z"],
            IntensityScale: null,
            Priority: 0,
            InlineMicrosensations:
            [
                new MicrosensationParameters(new Dictionary<string, ParameterValue>
                {
                    ["frequency"] = new ParameterValue.Number(50),
                }),
            ]);

    private sealed record System(TriggerCoordinator Coordinator);

    private sealed class StubBodyMap : IBodyMapState
    {
        private readonly string? _otherBackendId;

        public StubBodyMap(OverlapPolicy policy, string? alwaysOverlapWith)
        {
            OverlapPolicy = policy;
            _otherBackendId = alwaysOverlapWith;
        }

        public OverlapPolicy OverlapPolicy { get; }
        public int RefusedBackendCount => 0;
        public int PlacementCount => 0;
        public int WarningCount => 0;

        public OverlapHit? CheckOverlap(IHapticBackend backend, IReadOnlyList<string> zoneIds) =>
            _otherBackendId is null
                ? null
                : new OverlapHit(BodyRegion.ChestFront, _otherBackendId, zoneIds[0]);
    }
}

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

public class ConcurrencyPolicyTests
{
    [Fact]
    public async Task REJECT_NEW_returns_rate_limited_when_at_capacity()
    {
        var sys = BuildSystem(ConcurrencyPolicy.RejectNew, maxConcurrent: 1, sensationDuration: TimeSpan.FromSeconds(2));

        (await sys.Coordinator.TriggerAsync(MakeInput("a"), default))
            .Should().BeOfType<TriggerOutcome.Accepted>();

        var second = await sys.Coordinator.TriggerAsync(MakeInput("b"), default);

        var rejected = second.Should().BeOfType<TriggerOutcome.Rejected>().Subject;
        rejected.Code.Should().Be(TriggerErrorCode.RateLimited);
        rejected.Message.Should().Contain("REJECT_NEW");
    }

    [Fact]
    public async Task CANCEL_OLDEST_preempts_the_earliest_active_sensation()
    {
        var sys = BuildSystem(ConcurrencyPolicy.CancelOldest, maxConcurrent: 1, sensationDuration: TimeSpan.FromSeconds(5));

        var firstResult = await sys.Coordinator.TriggerAsync(MakeInput("a"), default);
        firstResult.Should().BeOfType<TriggerOutcome.Accepted>();
        sys.Backend.LastRequest.Should().NotBeNull();

        sys.Time.Advance(TimeSpan.FromMilliseconds(100));

        var secondResult = await sys.Coordinator.TriggerAsync(MakeInput("b"), default);

        secondResult.Should().BeOfType<TriggerOutcome.Accepted>();

        // The backend's CancellationToken for the first sensation should have been cancelled.
        sys.Backend.LastCancelledToken.Should().NotBeNull();
        sys.Backend.LastCancelledToken!.Value.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task PRIORITY_rejects_lower_priority_when_at_capacity()
    {
        var sys = BuildSystem(ConcurrencyPolicy.Priority, maxConcurrent: 1, sensationDuration: TimeSpan.FromSeconds(5));

        await sys.Coordinator.TriggerAsync(MakeInput("a", priority: 100), default);

        var lower = await sys.Coordinator.TriggerAsync(MakeInput("b", priority: 50), default);

        lower.Should().BeOfType<TriggerOutcome.Rejected>()
            .Which.Code.Should().Be(TriggerErrorCode.RateLimited);
    }

    [Fact]
    public async Task PRIORITY_preempts_lower_priority_when_higher_arrives()
    {
        var sys = BuildSystem(ConcurrencyPolicy.Priority, maxConcurrent: 1, sensationDuration: TimeSpan.FromSeconds(5));

        await sys.Coordinator.TriggerAsync(MakeInput("a", priority: 50), default);

        var higher = await sys.Coordinator.TriggerAsync(MakeInput("b", priority: 100), default);

        higher.Should().BeOfType<TriggerOutcome.Accepted>();
        sys.Backend.LastCancelledToken!.Value.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task QUEUE_waits_for_a_slot_then_admits()
    {
        var sys = BuildSystem(ConcurrencyPolicy.Queue, maxConcurrent: 1, sensationDuration: TimeSpan.FromSeconds(2));

        await sys.Coordinator.TriggerAsync(MakeInput("a"), default);

        var bTask = sys.Coordinator.TriggerAsync(MakeInput("b"), default);

        // Give the task a beat to settle on the semaphore wait.
        await Task.Delay(20);
        bTask.IsCompleted.Should().BeFalse();

        sys.Time.Advance(TimeSpan.FromSeconds(2));

        var bResult = await bTask.WaitAsync(TimeSpan.FromSeconds(2));
        bResult.Should().BeOfType<TriggerOutcome.Accepted>();
    }

    private static System BuildSystem(
        ConcurrencyPolicy policy,
        uint maxConcurrent,
        TimeSpan sensationDuration)
    {
        var sink = new RecordingEventSink();
        var time = new FakeTimeProvider();
        var registry = new BackendRegistry(sink, time);
        var library = new SensationLibrary(sink, time, Options.Create(new SmitedOptions()));
        var concurrency = new ConcurrencyEnforcer();

        var schema = new ParameterSchema();
        schema.Parameters.Add(new ParameterDef
        {
            Name = "frequency", Type = ParameterType.Number, Required = true, Min = 1, Max = 100, Description = "",
        });
        var topology = new ZoneTopology();
        topology.Zones.Add(new Zone { Id = "z", DisplayName = "Z" });

        var backend = new TrackingBackend("mock", schema, topology,
            new ConcurrencyModel { MaxConcurrent = maxConcurrent, Policy = policy },
            sensationDuration);
        registry.Register(backend);

        var coordinator = new TriggerCoordinator(
            registry, library, concurrency, new BodyMapState(), time,
            NullLogger<TriggerCoordinator>.Instance);

        return new System(coordinator, backend, time);
    }

    private static ResolvedTriggerInput MakeInput(string trace, int priority = 0) =>
        new(
            BackendId: "mock",
            SensationName: null,
            InlineMicrosensations: new[]
            {
                new MicrosensationParameters(new Dictionary<string, ParameterValue>
                {
                    ["frequency"] = new ParameterValue.Number(50),
                }),
            },
            ZoneIds: ["z"],
            IntensityScale: null,
            Priority: priority,
            ClientTraceId: trace);

    private sealed record System(
        TriggerCoordinator Coordinator,
        TrackingBackend Backend,
        FakeTimeProvider Time);

    private sealed class TrackingBackend : IHapticBackend
    {
        private readonly TimeSpan _duration;

        public TrackingBackend(
            string id,
            ParameterSchema schema,
            ZoneTopology topology,
            ConcurrencyModel concurrency,
            TimeSpan duration)
        {
            Id = id;
            Parameters = schema;
            Zones = topology;
            Concurrency = concurrency;
            _duration = duration;
        }

        public string Id { get; }
        public string Kind => "mock";
        public string DisplayName => "Mock";
        public BackendStatus Status => BackendStatus.Ready;
        public IReadOnlyList<string> Capabilities => new[] { "ems" };
        public ZoneTopology Zones { get; }
        public ParameterSchema Parameters { get; }
        public ConcurrencyModel Concurrency { get; }
        public CalibrationState? Calibration => null;
        public Google.Protobuf.WellKnownTypes.Struct? Extras => null;
        public IReadOnlySet<Smited.Daemon.BodyMap.BodyRegion> ForbiddenRegions { get; } =
            global::System.Collections.Immutable.ImmutableHashSet<Smited.Daemon.BodyMap.BodyRegion>.Empty;

        public BackendTriggerRequest? LastRequest { get; private set; }

        public CancellationToken? LastCancelledToken { get; private set; }

        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<BackendTriggerResult> TriggerAsync(BackendTriggerRequest request, CancellationToken ct)
        {
            LastRequest = request;
            ct.Register(() => LastCancelledToken = ct);
            return Task.FromResult(new BackendTriggerResult(request.SensationId, _duration));
        }

        public Task<int> StopAsync(BackendStopRequest request, CancellationToken ct) =>
            Task.FromResult(0);

        public IAsyncEnumerable<BackendEvent> Events => AsyncEmpty();

        private static async IAsyncEnumerable<BackendEvent> AsyncEmpty()
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Smited.Daemon.Backends;
using Smited.Daemon.BodyMap;
using Smited.Daemon.Configuration;
using Smited.Daemon.Events;
using Smited.Daemon.Tests.Fixtures;
using Smited.V1;
using Xunit;

namespace Smited.Daemon.Tests.Backends;

public class BackendBootstrapperBodyMapTests
{
    [Fact]
    public async Task Backend_with_smited_default_forbidden_zone_is_deregistered()
    {
        var bodyMap = new BodyMapOptions
        {
            Placements =
            {
                new Placement
                {
                    BackendId = "harness",
                    ZoneIds = { "z" },
                    Region = BodyRegion.Face,
                },
            },
        };

        await using var sys = await Build(bodyMap);

        sys.Registry.Count.Should().Be(0);
        sys.BodyMapState.RefusedBackendCount.Should().Be(1);
    }

    [Fact]
    public async Task Backend_with_overridable_forbidden_zone_is_kept_when_override_is_set()
    {
        var bodyMap = new BodyMapOptions
        {
            AllowOverrideRegions = { BodyRegion.Face },
            Placements =
            {
                new Placement
                {
                    BackendId = "harness",
                    ZoneIds = { "z" },
                    Region = BodyRegion.Face,
                },
            },
        };

        await using var sys = await Build(bodyMap);

        sys.Registry.Count.Should().Be(1);
        sys.BodyMapState.RefusedBackendCount.Should().Be(0);
    }

    [Fact]
    public async Task Backend_with_manufacturer_forbidden_zone_is_deregistered_even_with_override_set()
    {
        var bodyMap = new BodyMapOptions
        {
            // Override the smited default; the backend's own forbidden
            // list still wins.
            AllowOverrideRegions = { BodyRegion.Face },
            Placements =
            {
                new Placement
                {
                    BackendId = "harness",
                    ZoneIds = { "z" },
                    Region = BodyRegion.Face,
                },
            },
        };

        await using var sys = await Build(bodyMap, manufacturerForbidden: BodyRegion.Face);

        sys.Registry.Count.Should().Be(0);
        sys.BodyMapState.RefusedBackendCount.Should().Be(1);
    }

    [Fact]
    public async Task Unknown_backend_id_aborts_startup()
    {
        var bodyMap = new BodyMapOptions
        {
            Placements =
            {
                new Placement
                {
                    BackendId = "ghost",
                    ZoneIds = { "z" },
                    Region = BodyRegion.LeftUpperArm,
                },
            },
        };

        var act = async () => await Build(bodyMap);

        var ex = await act.Should().ThrowAsync<OptionsValidationException>();
        ex.Which.Failures.Should().Contain(s => s.Contains("'ghost'"));
    }

    [Fact]
    public async Task Body_map_state_records_warning_count_for_overlap()
    {
        var bodyMap = new BodyMapOptions
        {
            Placements =
            {
                new Placement
                {
                    BackendId = "harness",
                    ZoneIds = { "z" },
                    Region = BodyRegion.LeftUpperArm,
                },
                new Placement
                {
                    BackendId = "extra",
                    ZoneIds = { "x" },
                    Region = BodyRegion.LeftUpperArm,
                },
            },
        };

        await using var sys = await Build(bodyMap, additionalBackends: new[]
        {
            new FakeBackend("extra") { Zones = MakeTopology("x") },
        });

        sys.Registry.Count.Should().Be(2);
        sys.BodyMapState.RefusedBackendCount.Should().Be(0);
        sys.BodyMapState.WarningCount.Should().Be(1);
    }

    private static async Task<TestSystem> Build(
        BodyMapOptions bodyMap,
        BodyRegion? manufacturerForbidden = null,
        IEnumerable<IHapticBackend>? additionalBackends = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero));
        var sink = new RecordingEventSink();
        var registry = new BackendRegistry(sink, time);
        var bus = new EventBus(NullLogger<EventBus>.Instance);

        var harness = new FakeBackend("harness")
        {
            Zones = MakeTopology("z"),
            ForbiddenRegions = manufacturerForbidden is null
                ? ImmutableHashSet<BodyRegion>.Empty
                : ImmutableHashSet.Create(manufacturerForbidden.Value),
        };

        var configuration = new ConfigurationBuilder().Build();
        var options = Options.Create(new SmitedOptions { BodyMap = bodyMap });

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddLogging();
        var serviceProvider = services.BuildServiceProvider();

        var bodyMapValidator = new BodyMapValidator();
        var bodyMapState = new BodyMapState();

        var allAdditional = new List<IHapticBackend> { harness };
        if (additionalBackends is not null)
        {
            allAdditional.AddRange(additionalBackends);
        }

        var bootstrapper = new BackendBootstrapper(
            registry,
            bus,
            options,
            configuration,
            serviceProvider,
            factories: Array.Empty<IBackendFactory>(),
            additionalBackends: allAdditional,
            bodyMapValidator,
            bodyMapState,
            NullLogger<BackendBootstrapper>.Instance);

        await bootstrapper.StartAsync(CancellationToken.None);
        return new TestSystem(bootstrapper, registry, bodyMapState, serviceProvider);
    }

    private static ZoneTopology MakeTopology(params string[] zones)
    {
        var topology = new ZoneTopology();
        foreach (var z in zones)
        {
            topology.Zones.Add(new Zone { Id = z, DisplayName = z });
        }
        return topology;
    }

    private sealed class TestSystem : IAsyncDisposable
    {
        private readonly BackendBootstrapper _bootstrapper;
        private readonly ServiceProvider _services;

        public TestSystem(
            BackendBootstrapper bootstrapper,
            BackendRegistry registry,
            BodyMapState bodyMapState,
            ServiceProvider services)
        {
            _bootstrapper = bootstrapper;
            _services = services;
            Registry = registry;
            BodyMapState = bodyMapState;
        }

        public BackendRegistry Registry { get; }

        public BodyMapState BodyMapState { get; }

        public async ValueTask DisposeAsync()
        {
            await _bootstrapper.StopAsync(CancellationToken.None);
            await _services.DisposeAsync();
        }
    }
}

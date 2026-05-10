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
    public async Task Smited_default_forbidden_placement_aborts_startup()
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

        var act = async () => await Build(bodyMap);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("Body map has");
        ex.Which.Message.Should().Contain("fatal");
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
    }

    [Fact]
    public async Task Manufacturer_forbidden_placement_aborts_startup_even_with_override_set()
    {
        var bodyMap = new BodyMapOptions
        {
            // Override the smited default; the backend's own forbidden
            // list still wins, and now wins fatally rather than via
            // deregister-and-continue.
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

        var act = async () => await Build(bodyMap, manufacturerForbidden: BodyRegion.Face);

        await act.Should().ThrowAsync<InvalidOperationException>();
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

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Placement_for_declared_but_declined_backend_is_a_warning_not_a_failure()
    {
        // The harness backend is registered in the test's
        // additionalBackends DI seam; "owo-primary" is declared in
        // Items but its factory declines (no factory registered for
        // owo_skin in this test). The placement targets owo-primary;
        // BackendDeclined is non-fatal, so the daemon starts.
        var bodyMap = new BodyMapOptions
        {
            Placements =
            {
                new Placement
                {
                    BackendId = "owo-primary",
                    ZoneIds = { "pectoral_l" },
                    Region = BodyRegion.LeftUpperArm,
                },
            },
        };

        await using var sys = await Build(
            bodyMap,
            extraDescriptors: new[]
            {
                new BackendDescriptor
                {
                    Kind = "owo_skin",
                    Id = "owo-primary",
                    Enabled = true,
                },
            });

        // harness is registered (via additionalBackends), owo-primary
        // is not (no factory for owo_skin in this test).
        sys.Registry.Count.Should().Be(1);
        sys.Registry.TryGet("harness").Should().NotBeNull();
        sys.Registry.TryGet("owo-primary").Should().BeNull();
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
        sys.BodyMapState.WarningCount.Should().Be(1);
    }

    [Fact]
    public async Task Unspecified_placeholder_does_not_count_toward_PlacementCount()
    {
        // BodyRegion.Unspecified is documented as "not part of the
        // body map" — the validator drops Unspecified placements
        // entirely. The banner's "N placements" reading must agree:
        // a config containing only Unspecified placeholders should
        // render "Not configured (warnings off)", not "1 placements"
        // (which would imply the placement is being enforced).
        var bodyMap = new BodyMapOptions
        {
            Placements =
            {
                new Placement
                {
                    BackendId = "harness",
                    ZoneIds = { "z" },
                    Region = BodyRegion.Unspecified,
                },
            },
        };

        await using var sys = await Build(bodyMap);

        sys.Registry.Count.Should().Be(1);
        sys.BodyMapState.PlacementCount.Should().Be(0);
        sys.BodyMapState.WarningCount.Should().Be(0);
    }

    [Fact]
    public async Task PlacementCount_reflects_only_non_Unspecified_placements()
    {
        // Mixed config: one real placement and one Unspecified
        // placeholder. The banner should show 1, not 2.
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
                    BackendId = "harness",
                    ZoneIds = { "z" },
                    Region = BodyRegion.Unspecified,
                },
            },
        };

        await using var sys = await Build(bodyMap);

        sys.BodyMapState.PlacementCount.Should().Be(1);
    }

    private static async Task<TestSystem> Build(
        BodyMapOptions bodyMap,
        BodyRegion? manufacturerForbidden = null,
        IEnumerable<IHapticBackend>? additionalBackends = null,
        IReadOnlyList<BackendDescriptor>? extraDescriptors = null)
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
        var options = Options.Create(new SmitedOptions
        {
            BodyMap = bodyMap,
            Backends = new SmitedOptions.BackendsOptions
            {
                // extraDescriptors gives a test the ability to declare
                // backends without registering them — used to exercise
                // the BackendDeclined path. The `harness` backend is
                // injected via additionalBackends and not represented
                // by a descriptor; that's fine — the validator's
                // declaredIds includes only descriptor ids and the
                // additionalBackends seam is below the descriptor
                // path entirely (it's the existing test injection
                // mechanism for ad-hoc IHapticBackend instances).
                Items = extraDescriptors?.ToList() ?? new List<BackendDescriptor>(),
            },
        });

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

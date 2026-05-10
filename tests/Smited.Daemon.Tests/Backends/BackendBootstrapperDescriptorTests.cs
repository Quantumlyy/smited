using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Smited.Daemon.Backends;
using Smited.Daemon.Backends.Mock;
using Smited.Daemon.BodyMap;
using Smited.Daemon.Configuration;
using Smited.Daemon.Events;
using Smited.Daemon.Tests.Fixtures;
using Xunit;

namespace Smited.Daemon.Tests.Backends;

public class BackendBootstrapperDescriptorTests
{
    [Fact]
    public async Task Empty_items_synthesizes_default_mock_owo_descriptor()
    {
        // The bootstrapper's empty-Items fallback ships a default
        // mock-owo descriptor at startup so "just run the daemon"
        // works with no configuration. Tests that want to assert
        // "no backends registered" use a Disabled descriptor instead
        // (see Disabled_descriptor_is_skipped).
        await using var sys = await Build(items: Array.Empty<BackendDescriptor>());

        sys.Registry.Count.Should().Be(1);
        sys.Registry.TryGet("mock-owo").Should().NotBeNull();
        sys.Registry.TryGet("mock-owo")!.Kind.Should().Be("owo_skin");
    }

    [Fact]
    public async Task Non_empty_items_suppresses_the_default_mock_owo_synthesis()
    {
        // A user descriptor with a non-default id; the synthesized
        // default mock-owo must NOT also register (otherwise we'd
        // have two mock_owo singletons in flight).
        var items = new[]
        {
            new BackendDescriptor { Kind = "mock_owo", Id = "custom-mock", Enabled = true },
        };

        await using var sys = await Build(items);

        sys.Registry.Count.Should().Be(1);
        sys.Registry.TryGet("custom-mock").Should().NotBeNull();
        sys.Registry.TryGet("mock-owo").Should().BeNull();
    }

    [Fact]
    public async Task Single_mock_owo_descriptor_registers_via_factory()
    {
        var items = new[] { new BackendDescriptor { Kind = "mock_owo", Id = "mock-owo" } };

        await using var sys = await Build(items);

        sys.Registry.Count.Should().Be(1);
        sys.Registry.TryGet("mock-owo").Should().NotBeNull();
        sys.Registry.TryGet("mock-owo")!.Kind.Should().Be("owo_skin");
    }

    [Fact]
    public async Task Disabled_descriptor_is_skipped()
    {
        var items = new[]
        {
            new BackendDescriptor { Kind = "mock_owo", Id = "mock-owo", Enabled = false },
        };

        await using var sys = await Build(items);

        sys.Registry.Count.Should().Be(0);
    }

    [Fact]
    public async Task Unknown_kind_is_logged_and_skipped_without_aborting()
    {
        var items = new[]
        {
            new BackendDescriptor { Kind = "mock_owo", Id = "mock-owo" },
            new BackendDescriptor { Kind = "no_such_kind", Id = "ghost" },
        };

        await using var sys = await Build(items);

        sys.Registry.Count.Should().Be(1);
        sys.Registry.TryGet("mock-owo").Should().NotBeNull();
        sys.Registry.TryGet("ghost").Should().BeNull();
    }

    [Fact]
    public async Task Two_descriptors_of_the_same_kind_with_different_ids_both_register()
    {
        // Use a fictional kind whose factory creates a fresh FakeBackend
        // each call (mock_owo singleton can't multi-instance).
        var items = new[]
        {
            new BackendDescriptor { Kind = "fake_multi", Id = "alpha" },
            new BackendDescriptor { Kind = "fake_multi", Id = "beta" },
        };

        await using var sys = await Build(
            items,
            extraFactories: new IBackendFactory[] { new FakeMultiInstanceFactory() });

        sys.Registry.Count.Should().Be(2);
        sys.Registry.TryGet("alpha").Should().NotBeNull();
        sys.Registry.TryGet("beta").Should().NotBeNull();
    }

    [Fact]
    public async Task Duplicate_ids_abort_startup_with_a_clear_error()
    {
        var items = new[]
        {
            new BackendDescriptor { Kind = "mock_owo", Id = "mock-owo" },
            new BackendDescriptor { Kind = "owo_skin", Id = "mock-owo" },
        };

        var act = async () => await Build(items);

        var ex = await act.Should().ThrowAsync<OptionsValidationException>();
        ex.Which.Failures.Should().Contain(s => s.Contains("'mock-owo' is duplicated"));
    }

    [Fact]
    public async Task Two_mock_owo_descriptors_abort_startup()
    {
        var items = new[]
        {
            new BackendDescriptor { Kind = "mock_owo", Id = "mock-a" },
            new BackendDescriptor { Kind = "mock_owo", Id = "mock-b" },
        };

        var act = async () => await Build(items);

        var ex = await act.Should().ThrowAsync<OptionsValidationException>();
        ex.Which.Failures.Should().Contain(s => s.Contains("Kind 'mock_owo' may appear at most once"));
    }

    [Fact]
    public async Task Factory_throwing_aborts_startup_with_descriptor_context()
    {
        // The IBackendFactory contract: throws are user-fixable
        // misconfiguration (e.g. malformed Options), not environmental
        // decline. The bootstrapper must surface the throw as a
        // startup failure rather than skipping the backend silently —
        // otherwise a typo like Options:HeartbeatSeconds="abc" would
        // leave the daemon running without the requested backend with
        // only a log line as evidence.
        var items = new[]
        {
            new BackendDescriptor { Kind = "throws_on_create", Id = "boom", Enabled = true },
        };

        var act = async () => await Build(
            items,
            extraFactories: new IBackendFactory[] { new ThrowingFactory() });

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("'throws_on_create'");
        ex.Which.Message.Should().Contain("'boom'");
        ex.Which.InnerException.Should().NotBeNull();
        ex.Which.InnerException!.Message.Should().Contain("simulated config error");
    }

    [Fact]
    public async Task Empty_kind_aborts_startup()
    {
        var items = new[] { new BackendDescriptor { Id = "mock-owo" } };

        var act = async () => await Build(items);

        var ex = await act.Should().ThrowAsync<OptionsValidationException>();
        ex.Which.Failures.Should().Contain(s => s.Contains("Kind is required"));
    }

    private static async Task<TestSystem> Build(
        IReadOnlyList<BackendDescriptor> items,
        IEnumerable<IBackendFactory>? extraFactories = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero));
        var sink = new RecordingEventSink();
        var registry = new BackendRegistry(sink, time);
        var bus = new EventBus(NullLogger<EventBus>.Instance);

        var configBuilder = new ConfigurationBuilder().AddInMemoryCollection(BuildConfig(items));
        var configuration = configBuilder.Build();

        var options = Options.Create(new SmitedOptions
        {
            Backends = new SmitedOptions.BackendsOptions
            {
                Items = items.ToList(),
            },
        });

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddLogging();
        services.AddSingleton<MockOwoBackend>();
        var serviceProvider = services.BuildServiceProvider();

        var factories = new List<IBackendFactory> { new MockOwoBackendFactory() };
        if (extraFactories is not null)
        {
            factories.AddRange(extraFactories);
        }

        var bodyMapValidator = new BodyMapValidator();
        var bodyMapState = new BodyMapState();

        var bootstrapper = new BackendBootstrapper(
            registry,
            bus,
            options,
            configuration,
            serviceProvider,
            factories,
            additionalBackends: Array.Empty<IHapticBackend>(),
            bodyMapValidator,
            bodyMapState,
            NullLogger<BackendBootstrapper>.Instance);

        await bootstrapper.StartAsync(CancellationToken.None);
        return new TestSystem(bootstrapper, registry, serviceProvider);
    }

    private static Dictionary<string, string?> BuildConfig(IReadOnlyList<BackendDescriptor> items)
    {
        var dict = new Dictionary<string, string?>();
        for (var i = 0; i < items.Count; i++)
        {
            dict[$"Smited:Backends:Items:{i}:Kind"] = items[i].Kind;
            dict[$"Smited:Backends:Items:{i}:Id"] = items[i].Id;
            dict[$"Smited:Backends:Items:{i}:Enabled"] = items[i].Enabled.ToString();
            if (items[i].DisplayName is not null)
            {
                dict[$"Smited:Backends:Items:{i}:DisplayName"] = items[i].DisplayName;
            }
        }
        return dict;
    }

    private sealed class TestSystem : IAsyncDisposable
    {
        private readonly BackendBootstrapper _bootstrapper;
        private readonly ServiceProvider _services;

        public TestSystem(
            BackendBootstrapper bootstrapper,
            BackendRegistry registry,
            ServiceProvider services)
        {
            _bootstrapper = bootstrapper;
            _services = services;
            Registry = registry;
        }

        public BackendRegistry Registry { get; }

        public async ValueTask DisposeAsync()
        {
            await _bootstrapper.StopAsync(CancellationToken.None);
            await _services.DisposeAsync();
        }
    }

    private sealed class FakeMultiInstanceFactory : IBackendFactory
    {
        public string Kind => "fake_multi";

        public IHapticBackend? TryCreate(
            BackendDescriptor descriptor,
            IConfigurationSection optionsSection,
            IServiceProvider services,
            Microsoft.Extensions.Logging.ILogger logger) =>
            new FakeBackend(
                id: descriptor.Id,
                kind: "fake_multi",
                displayName: descriptor.DisplayName ?? descriptor.Id);
    }

    private sealed class ThrowingFactory : IBackendFactory
    {
        public string Kind => "throws_on_create";

        public IHapticBackend? TryCreate(
            BackendDescriptor descriptor,
            IConfigurationSection optionsSection,
            IServiceProvider services,
            Microsoft.Extensions.Logging.ILogger logger) =>
            throw new InvalidOperationException(
                "simulated config error: HeartbeatSeconds is not a number");
    }
}

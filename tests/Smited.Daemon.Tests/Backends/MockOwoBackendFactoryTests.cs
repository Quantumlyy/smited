using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Smited.Daemon.Backends;
using Smited.Daemon.Backends.Mock;
using Smited.Daemon.Configuration;
using Xunit;

namespace Smited.Daemon.Tests.Backends;

public class MockOwoBackendFactoryTests
{
    [Fact]
    public void Kind_is_mock_owo()
    {
        new MockOwoBackendFactory().Kind.Should().Be("mock_owo");
    }

    [Fact]
    public void Empty_descriptor_yields_singleton_with_default_id_and_displayname()
    {
        var sp = BuildServices(out _);
        var factory = new MockOwoBackendFactory();
        var descriptor = new BackendDescriptor { Kind = "mock_owo" };

        var backend = factory.TryCreate(descriptor, EmptySection(), sp, NullLogger.Instance);

        backend.Should().NotBeNull();
        backend!.Id.Should().Be("mock-owo");
        backend.DisplayName.Should().Be("Mock OWO Skin");
    }

    [Fact]
    public void Id_and_displayname_overrides_are_applied_when_provided()
    {
        var sp = BuildServices(out _);
        var factory = new MockOwoBackendFactory();
        var descriptor = new BackendDescriptor
        {
            Kind = "mock_owo",
            Id = "mock-alt",
            DisplayName = "Mock OWO Skin (alt)",
        };

        var backend = factory.TryCreate(descriptor, EmptySection(), sp, NullLogger.Instance);

        backend.Should().NotBeNull();
        backend!.Id.Should().Be("mock-alt");
        backend.DisplayName.Should().Be("Mock OWO Skin (alt)");
    }

    [Fact]
    public void Idempotent_TryCreate_returns_the_same_singleton_instance()
    {
        var sp = BuildServices(out _);
        var factory = new MockOwoBackendFactory();
        var descriptor = new BackendDescriptor { Kind = "mock_owo", Id = "mock-once" };

        var first = factory.TryCreate(descriptor, EmptySection(), sp, NullLogger.Instance);
        var second = factory.TryCreate(descriptor, EmptySection(), sp, NullLogger.Instance);

        first.Should().BeSameAs(second);
    }

    [Fact]
    public void Conflicting_second_override_throws_invalid_operation()
    {
        var sp = BuildServices(out _);
        var factory = new MockOwoBackendFactory();

        factory.TryCreate(
            new BackendDescriptor { Kind = "mock_owo", Id = "mock-first" },
            EmptySection(), sp, NullLogger.Instance);

        var act = () => factory.TryCreate(
            new BackendDescriptor { Kind = "mock_owo", Id = "mock-second" },
            EmptySection(), sp, NullLogger.Instance);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already overridden*");
    }

    private static IServiceProvider BuildServices(out FakeTimeProvider time)
    {
        time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero));
        var capturedTime = time;
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(capturedTime);
        services.AddLogging();
        services.AddSingleton<MockOwoBackend>();
        return services.BuildServiceProvider();
    }

    private static IConfigurationSection EmptySection() =>
        new ConfigurationBuilder().Build().GetSection("Empty");
}

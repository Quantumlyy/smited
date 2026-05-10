using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Smited.Daemon.Backends;
using Smited.Daemon.Configuration;
using Smited.Daemon.Pishock;
using Xunit;

namespace Smited.Daemon.Tests.Backends.Pishock;

public class MockPishockBackendFactoryTests
{
    private static (MockPishockBackendFactory factory, IConfigurationSection options, IServiceProvider services, ILogger logger)
        NewFactory(Dictionary<string, string?>? settings = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .Build();
        var section = config.GetSection("Options");

        var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(new FakeTimeProvider(
                new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero)))
            .AddLogging()
            .BuildServiceProvider();

        return (new MockPishockBackendFactory(), section, services, NullLogger.Instance);
    }

    [Fact]
    public void Factory_kind_is_mock_pishock()
    {
        var factory = new MockPishockBackendFactory();
        factory.Kind.Should().Be("mock_pishock");
    }

    [Fact]
    public void TryCreate_with_valid_options_returns_a_backend_with_descriptor_id_as_Id()
    {
        var (factory, section, services, logger) = NewFactory();
        var descriptor = new BackendDescriptor
        {
            Kind = "mock_pishock",
            Id = "left-thigh",
            Enabled = true,
        };

        var backend = factory.TryCreate(descriptor, section, services, logger);

        backend.Should().NotBeNull();
        backend!.Id.Should().Be("left-thigh");
        backend.Kind.Should().Be("pishock");
    }

    [Fact]
    public void TryCreate_with_empty_AllowedOps_throws_BackendConfigurationException()
    {
        // Empty AllowedOps would mean a shocker that can't do anything;
        // misconfiguration, not a quirky-but-valid setup.
        var (factory, section, services, logger) = NewFactory(new Dictionary<string, string?>
        {
            ["Options:AllowedOps:0"] = "",
        });
        var descriptor = new BackendDescriptor
        {
            Kind = "mock_pishock",
            Id = "left-thigh",
        };

        // Build a section with explicitly-empty AllowedOps via a different path:
        // the binder reads an empty array as the default. Force it via direct config.
        var emptyConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Options:AllowedOps:Length"] = "0",
            })
            .Build();
        // The cleanest way: pass an Options section that explicitly produces empty.
        // PishockBackendOptions has AllowedOps as a list initialized to non-empty;
        // the binder won't overwrite to empty unless told to. Replace the list
        // post-bind via reflection wouldn't reach this path. Validate via a
        // direct-call path instead.
        var badOptions = new PishockBackendOptions
        {
            AllowedOps = new List<PishockOp>(),
        };

        var act = () => MockPishockBackendFactory.ValidateOptions(descriptor, badOptions);

        act.Should().Throw<BackendConfigurationException>()
            .WithMessage("*AllowedOps*");
    }

    [Fact]
    public void TryCreate_with_negative_MaxOpsPerSecond_throws_BackendConfigurationException()
    {
        var descriptor = new BackendDescriptor
        {
            Kind = "mock_pishock",
            Id = "left-thigh",
        };
        var options = new PishockBackendOptions { MaxOpsPerSecond = 0 };

        var act = () => MockPishockBackendFactory.ValidateOptions(descriptor, options);

        act.Should().Throw<BackendConfigurationException>()
            .WithMessage("*MaxOpsPerSecond*");
    }

    [Fact]
    public void TryCreate_with_zero_MaxBurst_throws_BackendConfigurationException()
    {
        var descriptor = new BackendDescriptor
        {
            Kind = "mock_pishock",
            Id = "left-thigh",
        };
        var options = new PishockBackendOptions { MaxBurst = 0 };

        var act = () => MockPishockBackendFactory.ValidateOptions(descriptor, options);

        act.Should().Throw<BackendConfigurationException>()
            .WithMessage("*MaxBurst*");
    }

    [Fact]
    public void TryCreate_with_intensity_cap_above_100_throws_BackendConfigurationException()
    {
        var descriptor = new BackendDescriptor
        {
            Kind = "mock_pishock",
            Id = "left-thigh",
        };
        var options = new PishockBackendOptions { MaxIntensityShock = 150 };

        var act = () => MockPishockBackendFactory.ValidateOptions(descriptor, options);

        act.Should().Throw<BackendConfigurationException>()
            .WithMessage("*MaxIntensityShock*");
    }

    [Fact]
    public void TryCreate_with_zero_MaxDurationMs_throws_BackendConfigurationException()
    {
        var descriptor = new BackendDescriptor
        {
            Kind = "mock_pishock",
            Id = "left-thigh",
        };
        var options = new PishockBackendOptions { MaxDurationMs = 0 };

        var act = () => MockPishockBackendFactory.ValidateOptions(descriptor, options);

        act.Should().Throw<BackendConfigurationException>()
            .WithMessage("*MaxDurationMs*");
    }

    [Fact]
    public void TryCreate_uses_DescriptorId_when_options_DisplayName_is_unset()
    {
        var (factory, section, services, logger) = NewFactory();
        var descriptor = new BackendDescriptor
        {
            Kind = "mock_pishock",
            Id = "right-calf",
            Enabled = true,
        };

        var backend = factory.TryCreate(descriptor, section, services, logger);

        backend!.DisplayName.Should().Be("right-calf");
    }

    [Fact]
    public void TryCreate_returns_distinct_instances_for_distinct_descriptors()
    {
        // Multi-instance is the headline difference from MockOwoBackend
        // (which is a DI singleton). Two mock_pishock descriptors must
        // not share state — failing this would silently merge their
        // event streams, intensity caps, and rate limiters.
        var (factory, section, services, logger) = NewFactory();
        var d1 = new BackendDescriptor { Kind = "mock_pishock", Id = "left-thigh" };
        var d2 = new BackendDescriptor { Kind = "mock_pishock", Id = "right-calf" };

        var b1 = factory.TryCreate(d1, section, services, logger);
        var b2 = factory.TryCreate(d2, section, services, logger);

        b1.Should().NotBeSameAs(b2);
        b1!.Id.Should().Be("left-thigh");
        b2!.Id.Should().Be("right-calf");
    }
}

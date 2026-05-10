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

public class PishockBackendFactoryTests
{
    private static (PishockBackendFactory factory, IConfigurationSection section, IServiceProvider services, ILogger logger)
        NewFactory(Dictionary<string, string?> settings)
    {
        var prefixed = settings.ToDictionary(
            kv => "Options:" + kv.Key,
            kv => kv.Value);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(prefixed)
            .Build();
        var section = config.GetSection("Options");

        var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(new FakeTimeProvider(
                new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero)))
            .AddLogging()
            .BuildServiceProvider();

        return (new PishockBackendFactory(), section, services, NullLogger.Instance);
    }

    [Fact]
    public void Factory_kind_is_pishock()
    {
        new PishockBackendFactory().Kind.Should().Be("pishock");
    }

    [Fact]
    public void TryCreate_with_valid_cloud_options_returns_backend()
    {
        var (factory, section, services, logger) = NewFactory(new Dictionary<string, string?>
        {
            ["Mode"] = "Cloud",
            ["Username"] = "alice",
            ["ApiKey"] = "k1",
            ["ShareCode"] = "ABCD1234",
        });
        var descriptor = new BackendDescriptor { Kind = "pishock", Id = "left-thigh" };

        var backend = factory.TryCreate(descriptor, section, services, logger);

        backend.Should().NotBeNull();
        backend!.Id.Should().Be("left-thigh");
        backend.Kind.Should().Be("pishock");
    }

    [Fact]
    public void TryCreate_with_valid_lan_options_returns_backend()
    {
        var (factory, section, services, logger) = NewFactory(new Dictionary<string, string?>
        {
            ["Mode"] = "Lan",
            ["DeviceIp"] = "192.168.1.50",
        });
        var descriptor = new BackendDescriptor { Kind = "pishock", Id = "right-calf" };

        var backend = factory.TryCreate(descriptor, section, services, logger);

        backend.Should().NotBeNull();
        backend!.Id.Should().Be("right-calf");
    }

    [Fact]
    public void TryCreate_in_cloud_mode_without_Username_throws_with_field_named()
    {
        var (factory, section, services, logger) = NewFactory(new Dictionary<string, string?>
        {
            ["Mode"] = "Cloud",
            ["ApiKey"] = "k1",
            ["ShareCode"] = "ABCD1234",
        });
        var descriptor = new BackendDescriptor { Kind = "pishock", Id = "left-thigh" };

        var act = () => factory.TryCreate(descriptor, section, services, logger);

        act.Should().Throw<BackendConfigurationException>()
            .WithMessage("*Username*");
    }

    [Fact]
    public void TryCreate_in_cloud_mode_without_ApiKey_throws_with_field_named()
    {
        var (factory, section, services, logger) = NewFactory(new Dictionary<string, string?>
        {
            ["Mode"] = "Cloud",
            ["Username"] = "alice",
            ["ShareCode"] = "ABCD1234",
        });
        var descriptor = new BackendDescriptor { Kind = "pishock", Id = "left-thigh" };

        var act = () => factory.TryCreate(descriptor, section, services, logger);

        act.Should().Throw<BackendConfigurationException>()
            .WithMessage("*ApiKey*");
    }

    [Fact]
    public void TryCreate_in_cloud_mode_without_ShareCode_throws_with_field_named()
    {
        var (factory, section, services, logger) = NewFactory(new Dictionary<string, string?>
        {
            ["Mode"] = "Cloud",
            ["Username"] = "alice",
            ["ApiKey"] = "k1",
        });
        var descriptor = new BackendDescriptor { Kind = "pishock", Id = "left-thigh" };

        var act = () => factory.TryCreate(descriptor, section, services, logger);

        act.Should().Throw<BackendConfigurationException>()
            .WithMessage("*ShareCode*");
    }

    [Fact]
    public void TryCreate_in_lan_mode_without_DeviceIp_throws_with_field_named()
    {
        var (factory, section, services, logger) = NewFactory(new Dictionary<string, string?>
        {
            ["Mode"] = "Lan",
        });
        var descriptor = new BackendDescriptor { Kind = "pishock", Id = "right-calf" };

        var act = () => factory.TryCreate(descriptor, section, services, logger);

        act.Should().Throw<BackendConfigurationException>()
            .WithMessage("*DeviceIp*");
    }

    [Fact]
    public void TryCreate_returns_distinct_instances_for_distinct_descriptors()
    {
        var (factory, section, services, logger) = NewFactory(new Dictionary<string, string?>
        {
            ["Mode"] = "Cloud",
            ["Username"] = "alice",
            ["ApiKey"] = "k1",
            ["ShareCode"] = "ABCD1234",
        });
        var d1 = new BackendDescriptor { Kind = "pishock", Id = "alpha" };
        var d2 = new BackendDescriptor { Kind = "pishock", Id = "beta" };

        var b1 = factory.TryCreate(d1, section, services, logger);
        var b2 = factory.TryCreate(d2, section, services, logger);

        b1.Should().NotBeSameAs(b2);
    }
}

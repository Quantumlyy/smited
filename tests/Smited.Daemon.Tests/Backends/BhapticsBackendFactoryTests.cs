// Excluded from compile on non-Windows hosts via the test csproj's
// conditional <Compile Remove>. References BhapticsBackendFactory,
// which lives in the Windows-only Smited.Daemon.Bhaptics assembly.

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Smited.Daemon.Backends;
using Smited.Daemon.Bhaptics;
using Smited.Daemon.Configuration;
using Xunit;

namespace Smited.Daemon.Tests.Backends;

public class BhapticsBackendFactoryTests
{
    [Theory]
    [InlineData("bhaptics_vest", typeof(BhapticsVestBackend))]
    [InlineData("bhaptics_sleeve_l", typeof(BhapticsSleeveBackend))]
    [InlineData("bhaptics_sleeve_r", typeof(BhapticsSleeveBackend))]
    [InlineData("bhaptics_feet_l", typeof(BhapticsFeetBackend))]
    [InlineData("bhaptics_feet_r", typeof(BhapticsFeetBackend))]
    public void TryCreate_dispatches_kind_to_matching_backend_type(string kind, Type expectedType)
    {
        var backend = CreateBackend(kind, new BackendDescriptor
        {
            Kind = kind,
            Id = kind.Replace("_", "-"),
            Enabled = true,
        });

        backend.Should().BeOfType(expectedType);
        backend!.Kind.Should().Be(kind);
    }

    [Theory]
    [InlineData("bhaptics_sleeve_l", "sleeve_l")]
    [InlineData("bhaptics_sleeve_r", "sleeve_r")]
    [InlineData("bhaptics_feet_l", "feet_l")]
    [InlineData("bhaptics_feet_r", "feet_r")]
    public void TryCreate_sets_correct_device_key_for_side_specific_kinds(string kind, string expectedDeviceKey)
    {
        var backend = CreateBackend(kind, new BackendDescriptor
        {
            Kind = kind,
            Id = kind.Replace("_", "-"),
            Enabled = true,
        });

        ((BhapticsBackendBase)backend!).DeviceKey.Should().Be(expectedDeviceKey);
    }

    [Fact]
    public void TryCreate_applies_descriptor_DisplayName_override()
    {
        var backend = CreateBackend("bhaptics_vest", new BackendDescriptor
        {
            Kind = "bhaptics_vest",
            Id = "vest",
            DisplayName = "Living Room TactSuit",
            Enabled = true,
        });

        backend!.DisplayName.Should().Be("Living Room TactSuit");
    }

    [Fact]
    public void TryCreate_applies_descriptor_Id_override()
    {
        var backend = CreateBackend("bhaptics_vest", new BackendDescriptor
        {
            Kind = "bhaptics_vest",
            Id = "vest-custom",
            Enabled = true,
        });

        backend!.Id.Should().Be("vest-custom");
    }

    [Fact]
    public void Factory_constructed_with_unknown_kind_throws_on_TryCreate()
    {
        var factory = NewFactory("bhaptics_unknown");
        var descriptor = new BackendDescriptor { Kind = "bhaptics_unknown", Id = "x", Enabled = true };
        var section = new ConfigurationBuilder().AddInMemoryCollection().Build().GetSection("");

        var act = () => factory.TryCreate(descriptor, section, BuildServices(), NullLogger.Instance);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*unrecognised kind*");
    }

    [Fact]
    public void SupportedKinds_lists_every_routable_kind_exactly_once()
    {
        // This list is duplicated in BackendsServiceCollectionExtensions
        // (BhapticsKinds private field). If a future kind lands here it
        // MUST also land there or it will never be registered with DI.
        BhapticsBackendFactory.SupportedKinds.Should().BeEquivalentTo(new[]
        {
            "bhaptics_vest",
            "bhaptics_sleeve_l",
            "bhaptics_sleeve_r",
            "bhaptics_feet_l",
            "bhaptics_feet_r",
        });
        BhapticsBackendFactory.SupportedKinds.Should().OnlyHaveUniqueItems();
    }

    private static IHapticBackend? CreateBackend(string kind, BackendDescriptor descriptor)
    {
        var factory = NewFactory(kind);
        var section = new ConfigurationBuilder().AddInMemoryCollection().Build().GetSection("");
        return factory.TryCreate(descriptor, section, BuildServices(), NullLogger.Instance);
    }

    private static BhapticsBackendFactory NewFactory(string kind)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero));
        var sdk = Substitute.For<IBhapticsSdk>();
        var loggerFactory = NullLoggerFactory.Instance;
        return new BhapticsBackendFactory(kind, sdk, time, loggerFactory);
    }

    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(
            new FakeTimeProvider(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero)));
        services.AddSingleton(Substitute.For<IBhapticsSdk>());
        services.AddLogging();
        return services.BuildServiceProvider();
    }
}

// Excluded from compile on non-Windows hosts via the test csproj's
// conditional <Compile Remove>. References OwoBackendFactory and
// OwoBackend, which live in the Windows-only Smited.Daemon.Owo
// assembly.

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Smited.Daemon.Backends;
using Smited.Daemon.Configuration;
using Smited.Daemon.Owo;
using Xunit;

namespace Smited.Daemon.Tests.Backends;

public class OwoBackendFactoryTests
{
    [Fact]
    public void Kind_is_owo_skin()
    {
        new OwoBackendFactory().Kind.Should().Be("owo_skin");
    }

    [Fact]
    public void Factory_uses_default_display_name_when_descriptor_omits_it()
    {
        var backend = CreateBackend(new BackendDescriptor
        {
            Kind = "owo_skin",
            Id = "owo-primary",
            Enabled = true,
        });

        backend.DisplayName.Should().Be("OWO Skin");
    }

    [Fact]
    public void Factory_applies_descriptor_DisplayName_override()
    {
        // Regression for the parity gap with MockOwoBackendFactory:
        // the descriptor's DisplayName must reach the backend instance.
        var backend = CreateBackend(new BackendDescriptor
        {
            Kind = "owo_skin",
            Id = "owo-primary",
            DisplayName = "Living Room OWO",
            Enabled = true,
        });

        backend.DisplayName.Should().Be("Living Room OWO");
    }

    [Fact]
    public void Factory_applies_descriptor_Id_over_Options_BackendId()
    {
        // The descriptor.Id is what clients address over the wire; it
        // wins over whatever lives in Options.BackendId.
        var backend = CreateBackend(
            new BackendDescriptor
            {
                Kind = "owo_skin",
                Id = "owo-living-room",
                Enabled = true,
            },
            optionsConfig: new Dictionary<string, string?>
            {
                ["BackendId"] = "ignored-id",
                ["GameDisplayName"] = "smited haptic daemon",
            });

        backend.Id.Should().Be("owo-living-room");
    }

    private static OwoBackend CreateBackend(
        BackendDescriptor descriptor,
        IReadOnlyDictionary<string, string?>? optionsConfig = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero));
        var sdk = Substitute.For<IOwoSdk>();
        sdk.IsConnected.Returns(true);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(sdk);
        services.AddLogging();
        var sp = services.BuildServiceProvider();

        var section = new ConfigurationBuilder()
            .AddInMemoryCollection(optionsConfig ?? new Dictionary<string, string?>())
            .Build()
            .GetSection("");

        var factory = new OwoBackendFactory();
        var backend = factory.TryCreate(descriptor, section, sp, NullLogger.Instance);
        backend.Should().NotBeNull();
        return (OwoBackend)backend!;
    }
}

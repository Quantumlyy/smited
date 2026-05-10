using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Smited.Daemon.Backends;
using Smited.Daemon.Configuration;
using Xunit;

namespace Smited.Daemon.Tests.Configuration;

public class OwoBackendOptionsTests
{
    [Fact]
    public void Defaults_match_the_documented_values()
    {
        var options = new OwoBackendOptions();

        options.BackendId.Should().Be("owo-primary");
        options.GameDisplayName.Should().Be("smited haptic daemon");
        options.ManualIp.Should().BeNull();
        options.MaxReconnectAttempts.Should().Be(3);
        options.HeartbeatSeconds.Should().Be(5);
    }

    [Fact]
    public void All_fields_round_trip_through_the_descriptor_options_section()
    {
        // OwoBackendOptions now lives under a per-descriptor Options
        // sub-section rather than the legacy Smited:Backends:Owo path.
        // The OwoBackendFactory binds the matching section directly.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Smited:Backends:Items:0:Kind"] = "owo_skin",
                ["Smited:Backends:Items:0:Id"] = "owo-secondary",
                ["Smited:Backends:Items:0:Options:BackendId"] = "owo-secondary",
                ["Smited:Backends:Items:0:Options:GameDisplayName"] = "alt name",
                ["Smited:Backends:Items:0:Options:ManualIp"] = "192.168.1.7",
                ["Smited:Backends:Items:0:Options:MaxReconnectAttempts"] = "7",
                ["Smited:Backends:Items:0:Options:HeartbeatSeconds"] = "12",
            })
            .Build();

        var bound = configuration
            .GetSection("Smited:Backends:Items:0:Options")
            .Get<OwoBackendOptions>();

        bound.Should().NotBeNull();
        bound!.BackendId.Should().Be("owo-secondary");
        bound.GameDisplayName.Should().Be("alt name");
        bound.ManualIp.Should().Be("192.168.1.7");
        bound.MaxReconnectAttempts.Should().Be(7);
        bound.HeartbeatSeconds.Should().Be(12);
    }

    [Fact]
    public void Defaults_apply_when_options_section_is_omitted()
    {
        // A descriptor without an Options sub-section: the factory
        // falls back to `new OwoBackendOptions()`, so every field reads
        // the type's default value.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Smited:Backends:Items:0:Kind"] = "owo_skin",
                ["Smited:Backends:Items:0:Id"] = "owo-primary",
            })
            .Build();

        var bound = configuration
            .GetSection("Smited:Backends:Items:0:Options")
            .Get<OwoBackendOptions>() ?? new OwoBackendOptions();

        bound.BackendId.Should().Be("owo-primary");
        bound.GameDisplayName.Should().Be("smited haptic daemon");
        bound.ManualIp.Should().BeNull();
        bound.MaxReconnectAttempts.Should().Be(3);
        bound.HeartbeatSeconds.Should().Be(5);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.42.7")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    public void IsManualIpValid_accepts_unset_or_parseable_addresses(string? manualIp)
    {
        new OwoBackendOptions { ManualIp = manualIp }.IsManualIpValid().Should().BeTrue();
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("999.999.999.999")]
    [InlineData("hello world")]
    [InlineData("http://example.com")]
    public void IsManualIpValid_rejects_garbage(string manualIp)
    {
        new OwoBackendOptions { ManualIp = manualIp }.IsManualIpValid().Should().BeFalse();
    }
}

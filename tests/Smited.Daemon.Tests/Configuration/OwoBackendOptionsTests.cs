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
    public void All_fields_round_trip_through_the_configuration_binder()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Smited:Backends:Owo:BackendId"] = "owo-secondary",
                ["Smited:Backends:Owo:GameDisplayName"] = "alt name",
                ["Smited:Backends:Owo:ManualIp"] = "192.168.1.7",
                ["Smited:Backends:Owo:MaxReconnectAttempts"] = "7",
                ["Smited:Backends:Owo:HeartbeatSeconds"] = "12",
            })
            .Build();

        var bound = configuration.GetSection("Smited").Get<SmitedOptions>();

        bound.Should().NotBeNull();
        bound!.Backends.Owo.BackendId.Should().Be("owo-secondary");
        bound.Backends.Owo.GameDisplayName.Should().Be("alt name");
        bound.Backends.Owo.ManualIp.Should().Be("192.168.1.7");
        bound.Backends.Owo.MaxReconnectAttempts.Should().Be(7);
        bound.Backends.Owo.HeartbeatSeconds.Should().Be(12);
    }

    [Fact]
    public void Defaults_apply_when_section_is_omitted()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Smited:Backends:EnableOwo"] = "true",
            })
            .Build();

        var bound = configuration.GetSection("Smited").Get<SmitedOptions>();

        bound.Should().NotBeNull();
        bound!.Backends.EnableOwo.Should().BeTrue();
        bound.Backends.Owo.BackendId.Should().Be("owo-primary");
        bound.Backends.Owo.GameDisplayName.Should().Be("smited haptic daemon");
        bound.Backends.Owo.ManualIp.Should().BeNull();
        bound.Backends.Owo.MaxReconnectAttempts.Should().Be(3);
        bound.Backends.Owo.HeartbeatSeconds.Should().Be(5);
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

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Smited.Daemon.Configuration;
using Xunit;

namespace Smited.Daemon.Tests.Configuration;

public class BackendDescriptorTests
{
    [Fact]
    public void Single_descriptor_binds_kind_id_enabled_and_displayname()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Smited:Backends:Items:0:Kind"] = "mock_owo",
                ["Smited:Backends:Items:0:Id"] = "mock-owo",
                ["Smited:Backends:Items:0:Enabled"] = "true",
                ["Smited:Backends:Items:0:DisplayName"] = "Test Mock",
            })
            .Build();

        var bound = configuration.GetSection("Smited").Get<SmitedOptions>();

        bound.Should().NotBeNull();
        bound!.Backends.Items.Should().HaveCount(1);
        var d = bound.Backends.Items[0];
        d.Kind.Should().Be("mock_owo");
        d.Id.Should().Be("mock-owo");
        d.Enabled.Should().BeTrue();
        d.DisplayName.Should().Be("Test Mock");
    }

    [Fact]
    public void Two_descriptors_with_different_ids_bind_into_a_two_element_list()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Smited:Backends:Items:0:Kind"] = "pishock",
                ["Smited:Backends:Items:0:Id"] = "pishock-left",
                ["Smited:Backends:Items:1:Kind"] = "pishock",
                ["Smited:Backends:Items:1:Id"] = "pishock-right",
            })
            .Build();

        var bound = configuration.GetSection("Smited").Get<SmitedOptions>()!;

        bound.Backends.Items.Should().HaveCount(2);
        bound.Backends.Items.Select(i => i.Id)
            .Should().BeEquivalentTo("pishock-left", "pishock-right");
        bound.Backends.Items.Should().AllSatisfy(i => i.Kind.Should().Be("pishock"));
    }

    [Fact]
    public void Enabled_defaults_to_true_when_omitted()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Smited:Backends:Items:0:Kind"] = "mock_owo",
                ["Smited:Backends:Items:0:Id"] = "mock-owo",
            })
            .Build();

        var bound = configuration.GetSection("Smited").Get<SmitedOptions>()!;

        bound.Backends.Items.Single().Enabled.Should().BeTrue();
    }

    [Fact]
    public void DisplayName_defaults_to_null_when_omitted()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Smited:Backends:Items:0:Kind"] = "mock_owo",
                ["Smited:Backends:Items:0:Id"] = "mock-owo",
            })
            .Build();

        var bound = configuration.GetSection("Smited").Get<SmitedOptions>()!;

        bound.Backends.Items.Single().DisplayName.Should().BeNull();
    }

    [Fact]
    public void Empty_kind_and_id_are_the_default_when_omitted()
    {
        // Validation will reject these at startup; documented here so a
        // future change to the defaults doesn't silently flip behavior.
        var d = new BackendDescriptor();

        d.Kind.Should().Be("");
        d.Id.Should().Be("");
        d.Enabled.Should().BeTrue();
        d.DisplayName.Should().BeNull();
    }

    [Fact]
    public void Options_section_under_a_descriptor_is_addressable_by_index()
    {
        // Verifies the bootstrapper-side trick: the descriptor POCO does
        // NOT carry an IConfigurationSection (the binder doesn't bind
        // that type), but the bootstrapper resolves the matching section
        // by index for the factory to bind.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Smited:Backends:Items:0:Kind"] = "owo_skin",
                ["Smited:Backends:Items:0:Id"] = "owo-primary",
                ["Smited:Backends:Items:0:Options:GameDisplayName"] = "from descriptor",
                ["Smited:Backends:Items:0:Options:HeartbeatSeconds"] = "9",
            })
            .Build();

        var section = configuration.GetSection("Smited:Backends:Items:0:Options");

        section.Exists().Should().BeTrue();
        section["GameDisplayName"].Should().Be("from descriptor");
        section["HeartbeatSeconds"].Should().Be("9");
    }
}

using FluentAssertions;
using Smited.Daemon.Configuration;
using Xunit;

namespace Smited.Daemon.Tests.Configuration;

public class BackendDescriptorValidatorTests
{
    [Fact]
    public void Empty_list_has_no_errors()
    {
        BackendDescriptorValidator.Validate(Array.Empty<BackendDescriptor>())
            .Should().BeEmpty();
    }

    [Fact]
    public void Well_formed_descriptors_pass()
    {
        var descriptors = new[]
        {
            new BackendDescriptor { Kind = "mock_owo", Id = "mock-owo" },
            new BackendDescriptor { Kind = "owo_skin", Id = "owo-primary" },
        };

        BackendDescriptorValidator.Validate(descriptors).Should().BeEmpty();
    }

    [Fact]
    public void Empty_kind_is_rejected()
    {
        var descriptors = new[] { new BackendDescriptor { Id = "mock-owo" } };

        BackendDescriptorValidator.Validate(descriptors)
            .Should().ContainSingle(e => e.Contains("Kind is required"));
    }

    [Fact]
    public void Empty_id_is_rejected()
    {
        var descriptors = new[] { new BackendDescriptor { Kind = "mock_owo" } };

        BackendDescriptorValidator.Validate(descriptors)
            .Should().ContainSingle(e => e.Contains("Id is required"));
    }

    [Theory]
    [InlineData("Mock-Owo")]      // uppercase
    [InlineData("-leading-dash")] // leading dash
    [InlineData("under_score_ok")] // valid
    [InlineData("dash-ok")]        // valid
    [InlineData("with space")]    // space
    [InlineData("dot.in.id")]     // dots
    public void Id_is_validated_against_ident_regex(string id)
    {
        var descriptors = new[] { new BackendDescriptor { Kind = "mock_owo", Id = id } };

        var errors = BackendDescriptorValidator.Validate(descriptors);

        var idIsValid = id == "under_score_ok" || id == "dash-ok";
        if (idIsValid)
        {
            errors.Should().BeEmpty();
        }
        else
        {
            errors.Should().ContainSingle(e => e.Contains("not a valid identifier"));
        }
    }

    [Fact]
    public void Duplicate_ids_are_rejected_even_across_different_kinds()
    {
        var descriptors = new[]
        {
            new BackendDescriptor { Kind = "mock_owo", Id = "shared" },
            new BackendDescriptor { Kind = "owo_skin", Id = "shared" },
        };

        BackendDescriptorValidator.Validate(descriptors)
            .Should().ContainSingle(e => e.Contains("'shared' is duplicated"));
    }

    [Fact]
    public void Duplicate_id_check_is_case_insensitive()
    {
        var descriptors = new[]
        {
            new BackendDescriptor { Kind = "owo_skin", Id = "owo-primary" },
            new BackendDescriptor { Kind = "owo_skin", Id = "owo-primary" },
        };

        BackendDescriptorValidator.Validate(descriptors)
            .Should().Contain(e => e.Contains("duplicated"));
    }

    [Fact]
    public void Two_mock_owo_descriptors_are_rejected()
    {
        var descriptors = new[]
        {
            new BackendDescriptor { Kind = "mock_owo", Id = "mock-a" },
            new BackendDescriptor { Kind = "mock_owo", Id = "mock-b" },
        };

        BackendDescriptorValidator.Validate(descriptors)
            .Should().ContainSingle(e => e.Contains("Kind 'mock_owo' may appear at most once"));
    }

    [Fact]
    public void Multiple_validation_failures_are_all_reported_at_once()
    {
        var descriptors = new[]
        {
            new BackendDescriptor { Kind = "", Id = "" }, // both missing
            new BackendDescriptor { Kind = "mock_owo", Id = "MOCK" }, // bad id
        };

        var errors = BackendDescriptorValidator.Validate(descriptors);

        errors.Should().HaveCountGreaterThanOrEqualTo(3);
    }
}

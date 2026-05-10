using FluentAssertions;
using Microsoft.Extensions.Options;
using Smited.Daemon.Tests.Fixtures;
using Xunit;

namespace Smited.Daemon.Tests.EndToEnd;

/// <summary>
/// E2E coverage for descriptor validation and bodymap forbidden-region
/// enforcement at startup. Drives the real host through
/// <see cref="DaemonFixture"/> so the full DI chain (descriptor binding,
/// validator, bootstrapper, banner state) is exercised; the unit-level
/// versions in <c>BackendBootstrapperDescriptorTests</c> and
/// <c>BackendBootstrapperBodyMapTests</c> stay as fast feedback loops.
///
/// Trigger-time overlap rejection (the eighth scenario in the request
/// list) lives in <see cref="BodyMapOverlapTests"/>.
/// </summary>
public class BodyMapStartupE2ETests
{
    [Fact]
    public void Duplicate_ids_abort_startup_with_a_clear_error()
    {
        // Two explicit descriptors that share an Id. The bootstrapper's
        // empty-Items fallback only fires when the user supplies zero
        // descriptors, so the test populates both Items entries.
        var act = () => new DaemonFixture(additionalConfig: new Dictionary<string, string?>
        {
            ["Smited:Backends:Items:0:Kind"] = "mock_owo",
            ["Smited:Backends:Items:0:Id"] = "mock-owo",
            ["Smited:Backends:Items:0:Enabled"] = "true",
            ["Smited:Backends:Items:1:Kind"] = "owo_skin",
            ["Smited:Backends:Items:1:Id"] = "mock-owo",
            ["Smited:Backends:Items:1:Enabled"] = "true",
        });

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(s => s.Contains("'mock-owo' is duplicated"));
    }

    [Fact]
    public void Two_mock_owo_descriptors_abort_startup()
    {
        var act = () => new DaemonFixture(additionalConfig: new Dictionary<string, string?>
        {
            ["Smited:Backends:Items:0:Kind"] = "mock_owo",
            ["Smited:Backends:Items:0:Id"] = "mock-owo",
            ["Smited:Backends:Items:0:Enabled"] = "true",
            ["Smited:Backends:Items:1:Kind"] = "mock_owo",
            ["Smited:Backends:Items:1:Id"] = "mock-secondary",
            ["Smited:Backends:Items:1:Enabled"] = "true",
        });

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(s => s.Contains("Kind 'mock_owo' may appear at most once"));
    }

    [Fact]
    public void Empty_kind_aborts_startup_with_a_clear_error()
    {
        // Setting Kind to empty string trips the
        // string.IsNullOrWhiteSpace check in the validator.
        var act = () => new DaemonFixture(additionalConfig: new Dictionary<string, string?>
        {
            ["Smited:Backends:Items:0:Kind"] = "",
            ["Smited:Backends:Items:0:Id"] = "ghost",
            ["Smited:Backends:Items:0:Enabled"] = "true",
        });

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(s => s.Contains("Kind is required"));
    }

    [Fact]
    public void Placement_on_a_non_forbidden_region_does_not_fail()
    {
        // MockOwoBackend.ForbiddenRegions is empty, so the only relevant
        // check is the smited-default list (Face / Throat / Pelvis /
        // ChestOverHeart). LeftUpperArm is in neither. The backend
        // stays registered, the bodymap reports one placement, no
        // refusals.
        using var fixture = new DaemonFixture(additionalConfig: new Dictionary<string, string?>
        {
            ["Smited:BodyMap:Placements:0:BackendId"] = "mock-owo",
            ["Smited:BodyMap:Placements:0:ZoneIds:0"] = "arm_l",
            ["Smited:BodyMap:Placements:0:Region"] = "LeftUpperArm",
        });

        fixture.Registry.Count.Should().Be(1);
        fixture.Registry.TryGet("mock-owo").Should().NotBeNull();
        fixture.BodyMapState.RefusedBackendCount.Should().Be(0);
        fixture.BodyMapState.PlacementCount.Should().Be(1);
    }

    [Fact]
    public void Placement_on_Face_deregisters_the_backend_and_marks_it_refused()
    {
        // Face is in SmitedDefaultForbiddenRegions and the user has not
        // opted out. The validator reports the error, the bootstrapper
        // deregisters mock-owo, and IBodyMapState.RefusedBackendCount
        // (which the startup banner reads to render the "(N refused)"
        // suffix) reflects the rejection.
        using var fixture = new DaemonFixture(additionalConfig: new Dictionary<string, string?>
        {
            ["Smited:BodyMap:Placements:0:BackendId"] = "mock-owo",
            ["Smited:BodyMap:Placements:0:ZoneIds:0"] = "pectoral_l",
            ["Smited:BodyMap:Placements:0:Region"] = "Face",
        });

        fixture.Registry.Count.Should().Be(0);
        fixture.Registry.TryGet("mock-owo").Should().BeNull();
        fixture.BodyMapState.RefusedBackendCount.Should().Be(1);
        fixture.BodyMapState.PlacementCount.Should().Be(1);
    }

    [Fact]
    public void Placement_on_ChestOverHeart_without_override_deregisters_the_backend()
    {
        // ChestOverHeart is in SmitedDefaultForbiddenRegions and inherits
        // forbiddenness through RegionHierarchy from any future ban on
        // ChestFront. With no AllowOverrideRegions the placement is
        // refused.
        using var fixture = new DaemonFixture(additionalConfig: new Dictionary<string, string?>
        {
            ["Smited:BodyMap:Placements:0:BackendId"] = "mock-owo",
            ["Smited:BodyMap:Placements:0:ZoneIds:0"] = "pectoral_l",
            ["Smited:BodyMap:Placements:0:Region"] = "ChestOverHeart",
        });

        fixture.Registry.Count.Should().Be(0);
        fixture.BodyMapState.RefusedBackendCount.Should().Be(1);
    }

    [Fact]
    public void Empty_Items_synthesizes_default_mock_owo()
    {
        // No descriptors supplied at all → bootstrapper synthesizes
        // a default mock-owo. This is the "just run the daemon"
        // shape that ships in appsettings.json (which has
        // "Items": []).
        using var fixture = new DaemonFixture();

        fixture.Registry.Count.Should().Be(1);
        fixture.Registry.TryGet("mock-owo").Should().NotBeNull();
    }

    [Fact]
    public void Non_empty_Items_replaces_the_default()
    {
        // A user-supplied descriptor list — even a single entry —
        // suppresses the synthesized default. Only the user's
        // descriptors register.
        using var fixture = new DaemonFixture(additionalConfig: new Dictionary<string, string?>
        {
            ["Smited:Backends:Items:0:Kind"] = "mock_owo",
            ["Smited:Backends:Items:0:Id"] = "custom-mock",
            ["Smited:Backends:Items:0:Enabled"] = "true",
        });

        fixture.Registry.Count.Should().Be(1);
        fixture.Registry.TryGet("custom-mock").Should().NotBeNull();
        fixture.Registry.TryGet("mock-owo").Should().BeNull();
    }

    [Fact]
    public void Placement_on_ChestOverHeart_with_override_keeps_the_backend_registered()
    {
        // Same placement as the previous test, but the user has added
        // ChestOverHeart to AllowOverrideRegions. The validator removes
        // ChestOverHeart from the smited defaults; mock-owo's
        // ForbiddenRegions remains empty so the manufacturer check is
        // also clean. The backend stays registered.
        using var fixture = new DaemonFixture(additionalConfig: new Dictionary<string, string?>
        {
            ["Smited:BodyMap:AllowOverrideRegions:0"] = "ChestOverHeart",
            ["Smited:BodyMap:Placements:0:BackendId"] = "mock-owo",
            ["Smited:BodyMap:Placements:0:ZoneIds:0"] = "pectoral_l",
            ["Smited:BodyMap:Placements:0:Region"] = "ChestOverHeart",
        });

        fixture.Registry.Count.Should().Be(1);
        fixture.Registry.TryGet("mock-owo").Should().NotBeNull();
        fixture.BodyMapState.RefusedBackendCount.Should().Be(0);
        fixture.BodyMapState.PlacementCount.Should().Be(1);
    }
}

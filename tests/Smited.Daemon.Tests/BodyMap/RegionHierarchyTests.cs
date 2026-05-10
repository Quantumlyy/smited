using FluentAssertions;
using Smited.Daemon.BodyMap;
using Xunit;

namespace Smited.Daemon.Tests.BodyMap;

public class RegionHierarchyTests
{
    [Fact]
    public void Same_region_overlaps_itself()
    {
        RegionHierarchy.Overlaps(BodyRegion.ChestFront, BodyRegion.ChestFront)
            .Should().BeTrue();
    }

    [Fact]
    public void Parent_overlaps_child()
    {
        RegionHierarchy.Overlaps(BodyRegion.ChestFront, BodyRegion.ChestOverHeart)
            .Should().BeTrue();
    }

    [Fact]
    public void Child_overlaps_parent()
    {
        // Symmetry: this is the case the previous one-directional
        // ContainingRegions walker handled correctly. The new contract
        // says child→parent and parent→child must both return true.
        RegionHierarchy.Overlaps(BodyRegion.ChestOverHeart, BodyRegion.ChestFront)
            .Should().BeTrue();
    }

    [Fact]
    public void Unrelated_regions_do_not_overlap()
    {
        RegionHierarchy.Overlaps(BodyRegion.ChestFront, BodyRegion.LeftThigh)
            .Should().BeFalse();
    }

    [Fact]
    public void Sibling_subregions_do_not_overlap()
    {
        // No siblings exist in the hierarchy today; this asserts the
        // shape so a future change that accidentally pairs unrelated
        // regions fails loudly rather than silently broadening
        // forbidden-region enforcement.
        RegionHierarchy.Overlaps(BodyRegion.LeftHand, BodyRegion.RightHand)
            .Should().BeFalse();
    }

    [Fact]
    public void Unspecified_overlaps_only_itself()
    {
        RegionHierarchy.Overlaps(BodyRegion.Unspecified, BodyRegion.Unspecified)
            .Should().BeTrue();
        RegionHierarchy.Overlaps(BodyRegion.Unspecified, BodyRegion.ChestFront)
            .Should().BeFalse();
        RegionHierarchy.Overlaps(BodyRegion.ChestFront, BodyRegion.Unspecified)
            .Should().BeFalse();
    }
}

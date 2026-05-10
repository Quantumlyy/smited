using FluentAssertions;
using Smited.Daemon.BodyMap;
using Xunit;

namespace Smited.Daemon.Tests.BodyMap;

public class RegionHierarchyTests
{
    [Fact]
    public void ChestOverHeart_includes_itself_and_ChestFront()
    {
        RegionHierarchy.ContainingRegions(BodyRegion.ChestOverHeart)
            .Should().BeEquivalentTo(new[]
            {
                BodyRegion.ChestOverHeart,
                BodyRegion.ChestFront,
            });
    }

    [Fact]
    public void ChestFront_returns_only_itself()
    {
        RegionHierarchy.ContainingRegions(BodyRegion.ChestFront)
            .Should().BeEquivalentTo(new[] { BodyRegion.ChestFront });
    }

    [Fact]
    public void Unspecified_returns_only_itself()
    {
        // Degenerate but stable: the validator treats Unspecified as
        // "not in the body map," and ContainingRegions reflects that.
        RegionHierarchy.ContainingRegions(BodyRegion.Unspecified)
            .Should().BeEquivalentTo(new[] { BodyRegion.Unspecified });
    }

    [Fact]
    public void Leaf_regions_with_no_parents_return_only_themselves()
    {
        // Sanity-check several leaves so a future hierarchy edit that
        // accidentally pulls one of these into a parent is caught.
        var leaves = new[]
        {
            BodyRegion.LeftWrist,
            BodyRegion.RightHand,
            BodyRegion.Glutes,
            BodyRegion.LeftAnkle,
            BodyRegion.BackLower,
        };

        foreach (var leaf in leaves)
        {
            RegionHierarchy.ContainingRegions(leaf).Should().BeEquivalentTo(new[] { leaf });
        }
    }
}

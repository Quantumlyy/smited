using System.Collections.Immutable;
using System.Text.RegularExpressions;
using FluentAssertions;
using Smited.Daemon.Backends;
using Smited.Daemon.BodyMap;
using Smited.Daemon.Configuration;
using Smited.Daemon.Tests.Fixtures;
using Smited.V1;
using Xunit;

namespace Smited.Daemon.Tests.BodyMap;

public class BodyMapValidatorTests
{
    [Fact]
    public void Empty_options_against_any_backends_yields_no_errors_or_warnings()
    {
        var backend = MakeFake("alpha", zones: ["pectoral_l"]);

        var result = new BodyMapValidator().Validate(new[] { backend }, new BodyMapOptions());

        result.Errors.Should().BeEmpty();
        result.Warnings.Should().BeEmpty();
        result.RegionsByBackend.Should().BeEmpty();
        result.BackendsByRegion.Should().BeEmpty();
    }

    [Fact]
    public void Manufacturer_forbidden_region_produces_a_typed_error()
    {
        var backend = MakeFake("vest",
            zones: ["pectoral_l"],
            forbidden: BodyRegion.ChestFront);

        var options = new BodyMapOptions
        {
            Placements =
            {
                new Placement
                {
                    BackendId = "vest",
                    ZoneIds = { "pectoral_l" },
                    Region = BodyRegion.ChestFront,
                },
            },
        };

        var result = new BodyMapValidator().Validate(new[] { backend }, options);

        result.Errors.Should().ContainSingle()
            .Which.Kind.Should().Be(BodyMapErrorKind.ManufacturerForbidden);
    }

    [Fact]
    public void Smited_default_forbidden_region_produces_an_overridable_error()
    {
        var backend = MakeFake("vest", zones: ["pectoral_l"]);

        var options = new BodyMapOptions
        {
            Placements =
            {
                new Placement
                {
                    BackendId = "vest",
                    ZoneIds = { "pectoral_l" },
                    Region = BodyRegion.Face,
                },
            },
        };

        var result = new BodyMapValidator().Validate(new[] { backend }, options);

        result.Errors.Should().ContainSingle()
            .Which.Kind.Should().Be(BodyMapErrorKind.SmitedDefaultForbidden);
    }

    [Fact]
    public void Allow_override_regions_suppresses_smited_default_errors()
    {
        var backend = MakeFake("vest", zones: ["pectoral_l"]);

        var options = new BodyMapOptions
        {
            AllowOverrideRegions = { BodyRegion.Face },
            Placements =
            {
                new Placement
                {
                    BackendId = "vest",
                    ZoneIds = { "pectoral_l" },
                    Region = BodyRegion.Face,
                },
            },
        };

        var result = new BodyMapValidator().Validate(new[] { backend }, options);

        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Allow_override_does_not_suppress_manufacturer_forbidden_errors()
    {
        // Manufacturer-mandated bans are non-overridable: even with the
        // user explicitly opting in to override Face, a backend that
        // declares Face in its ForbiddenRegions still rejects the
        // placement.
        var backend = MakeFake("vest",
            zones: ["pectoral_l"],
            forbidden: BodyRegion.Face);

        var options = new BodyMapOptions
        {
            AllowOverrideRegions = { BodyRegion.Face },
            Placements =
            {
                new Placement
                {
                    BackendId = "vest",
                    ZoneIds = { "pectoral_l" },
                    Region = BodyRegion.Face,
                },
            },
        };

        var result = new BodyMapValidator().Validate(new[] { backend }, options);

        result.Errors.Should().ContainSingle()
            .Which.Kind.Should().Be(BodyMapErrorKind.ManufacturerForbidden);
    }

    [Fact]
    public void Subregion_inherits_parent_forbidden_status()
    {
        // Backend bans ChestFront (parent); placement targets
        // ChestOverHeart (child). Hierarchy walk should still produce
        // a ManufacturerForbidden error.
        var backend = MakeFake("vest",
            zones: ["pectoral_l"],
            forbidden: BodyRegion.ChestFront);

        var options = new BodyMapOptions
        {
            Placements =
            {
                new Placement
                {
                    BackendId = "vest",
                    ZoneIds = { "pectoral_l" },
                    Region = BodyRegion.ChestOverHeart,
                },
            },
        };

        var result = new BodyMapValidator().Validate(new[] { backend }, options);

        result.Errors.Should().ContainSingle()
            .Which.Kind.Should().Be(BodyMapErrorKind.ManufacturerForbidden);
    }

    [Fact]
    public void Placement_in_ChestFront_fails_default_forbidden_check_for_ChestOverHeart()
    {
        // Regression for the cardiac-safety bypass. ChestOverHeart is in
        // SmitedDefaultForbiddenRegions; ChestFront contains it. A
        // placement declared in ChestFront overlaps ChestOverHeart and
        // must trip the default forbidden check, even though the
        // placement's region is the broader parent.
        var backend = MakeFake("vest", zones: ["pectoral_l"]);

        var options = new BodyMapOptions
        {
            Placements =
            {
                new Placement
                {
                    BackendId = "vest",
                    ZoneIds = { "pectoral_l" },
                    Region = BodyRegion.ChestFront,
                },
            },
        };

        var result = new BodyMapValidator().Validate(new[] { backend }, options);

        result.Errors.Should().Contain(e =>
            e.Kind == BodyMapErrorKind.SmitedDefaultForbidden
            && e.Region == BodyRegion.ChestOverHeart);
    }

    [Fact]
    public void ChestFront_and_ChestOverHeart_placements_warn_as_overlap()
    {
        // Cross-backend overlap regression. backend-a in ChestFront,
        // backend-b in ChestOverHeart — anatomically overlapping. The
        // pre-fix code only matched identical regions, so this pair was
        // silently treated as disjoint. With AllowOverrideRegions the
        // smited-default error is suppressed so the test asserts the
        // overlap-warning shape in isolation.
        var a = MakeFake("backend-a", zones: ["pectoral_l"]);
        var b = MakeFake("backend-b", zones: ["pectoral_l"]);

        var options = new BodyMapOptions
        {
            AllowOverrideRegions = { BodyRegion.ChestOverHeart },
            Placements =
            {
                new Placement
                {
                    BackendId = "backend-a",
                    ZoneIds = { "pectoral_l" },
                    Region = BodyRegion.ChestFront,
                },
                new Placement
                {
                    BackendId = "backend-b",
                    ZoneIds = { "pectoral_l" },
                    Region = BodyRegion.ChestOverHeart,
                },
            },
        };

        var result = new BodyMapValidator().Validate(new[] { a, b }, options);

        result.Warnings.Should().HaveCount(1);
        result.Warnings.Should().ContainSingle(w =>
            w.Region == BodyRegion.ChestFront || w.Region == BodyRegion.ChestOverHeart);
    }

    [Fact]
    public void Two_placements_in_same_region_emit_an_overlap_warning()
    {
        var a = MakeFake("a", zones: ["arm_l"]);
        var b = MakeFake("b", zones: ["arm_l"]);

        var options = new BodyMapOptions
        {
            Placements =
            {
                new Placement
                {
                    BackendId = "a",
                    ZoneIds = { "arm_l" },
                    Region = BodyRegion.LeftUpperArm,
                },
                new Placement
                {
                    BackendId = "b",
                    ZoneIds = { "arm_l" },
                    Region = BodyRegion.LeftUpperArm,
                },
            },
        };

        var result = new BodyMapValidator().Validate(new[] { a, b }, options);

        result.Errors.Should().BeEmpty();
        result.Warnings.Should().ContainSingle()
            .Which.Region.Should().Be(BodyRegion.LeftUpperArm);
        result.BackendsByRegion[BodyRegion.LeftUpperArm]
            .Should().BeEquivalentTo("a", "b");
    }

    [Fact]
    public void Overlap_warnings_are_suppressed_under_off_policy()
    {
        var a = MakeFake("a", zones: ["arm_l"]);
        var b = MakeFake("b", zones: ["arm_l"]);

        var options = new BodyMapOptions
        {
            OverlapPolicy = OverlapPolicy.Off,
            Placements =
            {
                new Placement { BackendId = "a", ZoneIds = { "arm_l" }, Region = BodyRegion.LeftUpperArm },
                new Placement { BackendId = "b", ZoneIds = { "arm_l" }, Region = BodyRegion.LeftUpperArm },
            },
        };

        var result = new BodyMapValidator().Validate(new[] { a, b }, options);

        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Unknown_backend_id_produces_an_unknown_backend_error()
    {
        var backend = MakeFake("vest", zones: ["pectoral_l"]);

        var options = new BodyMapOptions
        {
            Placements =
            {
                new Placement
                {
                    BackendId = "missing",
                    ZoneIds = { "pectoral_l" },
                    Region = BodyRegion.LeftUpperArm,
                },
            },
        };

        var result = new BodyMapValidator().Validate(new[] { backend }, options);

        result.Errors.Should().ContainSingle()
            .Which.Kind.Should().Be(BodyMapErrorKind.UnknownBackend);
    }

    [Fact]
    public void Placement_for_declared_but_declined_backend_is_BackendDeclined()
    {
        // Backend is declared in Items but its factory declined to
        // register it (e.g. owo_skin descriptor on a Mac host). The
        // placement targets it; that should be a non-fatal warning,
        // NOT a fatal UnknownBackend error.
        var registered = new[] { MakeFake("mock-owo", zones: ["pectoral_l"]) };
        var declared = new[] { "mock-owo", "owo-primary" }; // owo declined

        var options = new BodyMapOptions
        {
            Placements =
            {
                new Placement
                {
                    BackendId = "owo-primary",
                    ZoneIds = { "pectoral_l" },
                    Region = BodyRegion.LeftUpperArm,
                },
            },
        };

        var result = new BodyMapValidator().Validate(registered, declared, options);

        result.Errors.Should().Contain(e => e.Kind == BodyMapErrorKind.BackendDeclined);
        result.Errors.Should().NotContain(e => e.Kind == BodyMapErrorKind.UnknownBackend);
    }

    [Fact]
    public void Placement_for_undeclared_backend_is_UnknownBackend_with_did_you_mean_hint()
    {
        // Backend id is a typo: "owo-pirmary" instead of "owo-primary".
        // It's not registered AND not declared → UnknownBackend, fatal.
        // The error message lists the declared ids so the user knows
        // what to fix.
        var registered = new[] { MakeFake("mock-owo", zones: ["pectoral_l"]) };
        var declared = new[] { "mock-owo", "owo-primary" };

        var options = new BodyMapOptions
        {
            Placements =
            {
                new Placement
                {
                    BackendId = "owo-pirmary",
                    ZoneIds = { "pectoral_l" },
                    Region = BodyRegion.LeftUpperArm,
                },
            },
        };

        var result = new BodyMapValidator().Validate(registered, declared, options);

        var error = result.Errors.Should().ContainSingle().Subject;
        error.Kind.Should().Be(BodyMapErrorKind.UnknownBackend);
        error.Message.Should().Contain("Did you mean");
        error.Message.Should().Contain("owo-primary");
    }

    [Fact]
    public void Unknown_backend_with_no_declared_ids_omits_did_you_mean_list()
    {
        // Edge case: Items is empty AND a placement references some id.
        // The "did you mean" hint should degrade gracefully rather than
        // emitting "Did you mean one of: ".
        var registered = Array.Empty<IHapticBackend>();
        var declared = Array.Empty<string>();

        var options = new BodyMapOptions
        {
            Placements =
            {
                new Placement
                {
                    BackendId = "ghost",
                    ZoneIds = { "z" },
                    Region = BodyRegion.LeftUpperArm,
                },
            },
        };

        var result = new BodyMapValidator().Validate(registered, declared, options);

        var error = result.Errors.Should().ContainSingle().Subject;
        error.Kind.Should().Be(BodyMapErrorKind.UnknownBackend);
        error.Message.Should().NotContain("Did you mean");
        error.Message.Should().Contain("No backends are declared");
    }

    [Fact]
    public void Unknown_zone_id_produces_an_unknown_zone_error()
    {
        var backend = MakeFake("vest", zones: ["pectoral_l"]);

        var options = new BodyMapOptions
        {
            Placements =
            {
                new Placement
                {
                    BackendId = "vest",
                    ZoneIds = { "no_such_zone" },
                    Region = BodyRegion.LeftUpperArm,
                },
            },
        };

        var result = new BodyMapValidator().Validate(new[] { backend }, options);

        result.Errors.Should().ContainSingle()
            .Which.Kind.Should().Be(BodyMapErrorKind.UnknownZone);
    }

    [Fact]
    public void Group_id_in_placement_expands_to_member_zones_in_index()
    {
        var topology = new ZoneTopology();
        topology.Zones.Add(new Zone { Id = "arm_l", DisplayName = "L" });
        topology.Zones.Add(new Zone { Id = "arm_r", DisplayName = "R" });
        var grp = new ZoneGroup { Id = "arms", DisplayName = "Arms" };
        grp.ZoneIds.AddRange(new[] { "arm_l", "arm_r" });
        topology.Groups.Add(grp);
        var backend = new FakeBackend("vest") { Zones = topology };

        var options = new BodyMapOptions
        {
            Placements =
            {
                new Placement
                {
                    BackendId = "vest",
                    ZoneIds = { "arms" },
                    Region = BodyRegion.LeftUpperArm,
                },
            },
        };

        var result = new BodyMapValidator().Validate(new[] { backend }, options);

        result.Errors.Should().BeEmpty();
        result.RegionsByBackend["vest"].Should().Contain(BodyRegion.LeftUpperArm);
    }

    [Fact]
    public void Unspecified_region_is_silently_skipped()
    {
        var backend = MakeFake("vest", zones: ["pectoral_l"]);

        var options = new BodyMapOptions
        {
            Placements =
            {
                new Placement
                {
                    BackendId = "vest",
                    ZoneIds = { "pectoral_l" },
                    Region = BodyRegion.Unspecified,
                },
            },
        };

        var result = new BodyMapValidator().Validate(new[] { backend }, options);

        result.Errors.Should().BeEmpty();
        result.RegionsByBackend.Should().BeEmpty();
    }

    [Fact]
    public void Same_zone_in_two_placements_with_different_regions_is_DuplicateZonePlacement()
    {
        var backend = MakeFake("vest", zones: ["pectoral_l"]);

        var options = new BodyMapOptions
        {
            // Both placements would otherwise be valid; the duplicate
            // (pectoral_l in two different regions) is what we're
            // catching. AllowOverrideRegions suppresses the
            // ChestOverHeart smited-default error so it doesn't drown
            // out the duplicate error in the assertion.
            AllowOverrideRegions = { BodyRegion.ChestOverHeart },
            Placements =
            {
                new Placement
                {
                    BackendId = "vest",
                    ZoneIds = { "pectoral_l" },
                    Region = BodyRegion.ChestFront,
                },
                new Placement
                {
                    BackendId = "vest",
                    ZoneIds = { "pectoral_l" },
                    Region = BodyRegion.LeftUpperArm,
                },
            },
        };

        var result = new BodyMapValidator().Validate(new[] { backend }, options);

        var dup = result.Errors.Should().ContainSingle(
            e => e.Kind == BodyMapErrorKind.DuplicateZonePlacement).Subject;
        dup.ZoneId.Should().Be("pectoral_l");
        dup.Message.Should().Contain("ChestFront");
        dup.Message.Should().Contain("LeftUpperArm");
    }

    [Fact]
    public void Group_and_leaf_zone_overlap_is_caught_post_expansion()
    {
        // The "torso" group on MockOwoBackend's zone topology contains
        // pectoral_l (among others). Placement A declares the group in
        // LeftUpperArm; placement B declares the leaf in BackUpper.
        // Post-expansion, pectoral_l appears in both regions →
        // DuplicateZonePlacement. The neither-region-is-forbidden
        // setup keeps the test focused on duplicate detection rather
        // than incidentally tripping a forbidden-region error too.
        var topology = new ZoneTopology();
        topology.Zones.Add(new Zone { Id = "pectoral_l", DisplayName = "L" });
        topology.Zones.Add(new Zone { Id = "pectoral_r", DisplayName = "R" });
        var torso = new ZoneGroup { Id = "torso", DisplayName = "Torso" };
        torso.ZoneIds.AddRange(new[] { "pectoral_l", "pectoral_r" });
        topology.Groups.Add(torso);
        var backend = new FakeBackend("vest") { Zones = topology };

        var options = new BodyMapOptions
        {
            Placements =
            {
                new Placement
                {
                    BackendId = "vest",
                    ZoneIds = { "torso" },
                    Region = BodyRegion.LeftUpperArm,
                },
                new Placement
                {
                    BackendId = "vest",
                    ZoneIds = { "pectoral_l" },
                    Region = BodyRegion.BackUpper,
                },
            },
        };

        var result = new BodyMapValidator().Validate(new[] { backend }, options);

        result.Errors.Should().Contain(
            e => e.Kind == BodyMapErrorKind.DuplicateZonePlacement
              && e.ZoneId == "pectoral_l");
    }

    [Fact]
    public void Empty_ZoneIds_produces_EmptyPlacement_error()
    {
        // The Placement contract is "one or more zones." An empty
        // ZoneIds list silently produced zero expanded entries (no
        // errors, no index contributions) but still inflated the
        // banner's placement count. EmptyPlacement is fatal so the
        // misconfiguration surfaces.
        var backend = MakeFake("vest", zones: ["pectoral_l"]);

        var options = new BodyMapOptions
        {
            Placements =
            {
                new Placement
                {
                    BackendId = "vest",
                    ZoneIds = new List<string>(),
                    Region = BodyRegion.LeftThigh,
                },
            },
        };

        var result = new BodyMapValidator().Validate(new[] { backend }, options);

        result.Errors.Should().Contain(
            e => e.Kind == BodyMapErrorKind.EmptyPlacement
              && e.BackendId == "vest");
    }

    [Fact]
    public void Null_ZoneIds_produces_EmptyPlacement_error()
    {
        // Defensive: a JSON config that omits ZoneIds entirely could
        // bind the field to null on the Placement record despite
        // List<string>'s default initializer.
        var backend = MakeFake("vest", zones: ["pectoral_l"]);

        var options = new BodyMapOptions
        {
            Placements =
            {
                new Placement
                {
                    BackendId = "vest",
                    ZoneIds = null!,
                    Region = BodyRegion.LeftThigh,
                },
            },
        };

        var result = new BodyMapValidator().Validate(new[] { backend }, options);

        result.Errors.Should().Contain(
            e => e.Kind == BodyMapErrorKind.EmptyPlacement);
    }

    [Fact]
    public void Null_ZoneIds_placement_does_not_crash_overlap_detection()
    {
        // The previous round added the empty-zones gate at the top of
        // validation, but the overlap-warning pass kept walking
        // options.Placements directly and called SelectMany on
        // p.ZoneIds — null crashed the whole validation, masking the
        // structured EmptyPlacement error the user should have seen.
        // Both errors must surface together; the validator's job is
        // to accumulate.
        var a = MakeFake("backend-a", zones: ["pectoral_l"]);
        var b = MakeFake("backend-b", zones: ["pectoral_l"]);

        var options = new BodyMapOptions
        {
            AllowOverrideRegions = { BodyRegion.ChestOverHeart },
            Placements =
            {
                new Placement
                {
                    BackendId = "backend-a",
                    ZoneIds = { "pectoral_l" },
                    Region = BodyRegion.ChestFront,
                },
                new Placement
                {
                    BackendId = "backend-b",
                    ZoneIds = null!,
                    Region = BodyRegion.ChestFront,
                },
            },
        };

        // Pre-fix: throws NullReferenceException inside
        // BuildOverlapWarnings → ZoneIdsFor → SelectMany(p.ZoneIds).
        // Post-fix: returns errors cleanly.
        var result = new BodyMapValidator().Validate(new[] { a, b }, options);

        result.Errors.Should().Contain(e => e.Kind == BodyMapErrorKind.EmptyPlacement);
    }

    [Fact]
    public void Empty_ZoneIds_placement_with_overlap_does_not_crash()
    {
        // Same shape with empty list instead of null, since the
        // user-visible failure mode is identical.
        var a = MakeFake("backend-a", zones: ["pectoral_l"]);
        var b = MakeFake("backend-b", zones: ["pectoral_l"]);

        var options = new BodyMapOptions
        {
            AllowOverrideRegions = { BodyRegion.ChestOverHeart },
            Placements =
            {
                new Placement
                {
                    BackendId = "backend-a",
                    ZoneIds = { "pectoral_l" },
                    Region = BodyRegion.ChestFront,
                },
                new Placement
                {
                    BackendId = "backend-b",
                    ZoneIds = new List<string>(),
                    Region = BodyRegion.ChestFront,
                },
            },
        };

        var result = new BodyMapValidator().Validate(new[] { a, b }, options);

        result.Errors.Should().Contain(e => e.Kind == BodyMapErrorKind.EmptyPlacement);
    }

    [Fact]
    public void Same_zone_with_different_casing_is_DuplicateZonePlacement()
    {
        // GroupBy on the (BackendId, ZoneId) tuple defaulted to
        // case-sensitive equality, so PECTORAL_L vs pectoral_l slipped
        // past the duplicate check while the case-insensitive
        // ZoneRegions index downstream silently overwrote one entry
        // with the other. The custom BackendZoneKeyComparer enforces
        // OrdinalIgnoreCase on both members so detection and storage
        // agree.
        var backend = MakeFake("vest", zones: ["pectoral_l"]);

        var options = new BodyMapOptions
        {
            // AllowOverrideRegions keeps the smited-default cardiac
            // error from drowning out the duplicate assertion.
            AllowOverrideRegions = { BodyRegion.ChestOverHeart },
            Placements =
            {
                new Placement
                {
                    BackendId = "vest",
                    ZoneIds = { "pectoral_l" },
                    Region = BodyRegion.ChestFront,
                },
                new Placement
                {
                    BackendId = "vest",
                    ZoneIds = { "PECTORAL_L" },
                    Region = BodyRegion.LeftThigh,
                },
            },
        };

        var result = new BodyMapValidator().Validate(new[] { backend }, options);

        result.Errors.Should().Contain(
            e => e.Kind == BodyMapErrorKind.DuplicateZonePlacement);
    }

    [Fact]
    public void Same_backend_id_with_different_casing_is_DuplicateZonePlacement()
    {
        // Paranoid but cheap: backend ids should also collapse
        // case-insensitively at the duplicate-detection key. The
        // gRPC IDENT regex doesn't permit uppercase so this is
        // unreachable from real clients, but the validator operates
        // on user config which is free-form.
        var backend = MakeFake("owo-primary", zones: ["pectoral_l"]);

        var options = new BodyMapOptions
        {
            AllowOverrideRegions = { BodyRegion.ChestOverHeart },
            Placements =
            {
                new Placement
                {
                    BackendId = "owo-primary",
                    ZoneIds = { "pectoral_l" },
                    Region = BodyRegion.ChestFront,
                },
                new Placement
                {
                    BackendId = "OWO-PRIMARY",
                    ZoneIds = { "pectoral_l" },
                    Region = BodyRegion.LeftThigh,
                },
            },
        };

        var result = new BodyMapValidator().Validate(new[] { backend }, options);

        result.Errors.Should().Contain(
            e => e.Kind == BodyMapErrorKind.DuplicateZonePlacement);
    }

    [Fact]
    public void Same_zone_same_region_declared_twice_is_still_DuplicateZonePlacement()
    {
        // Redundant placement: two entries covering the same (backend,
        // zone, region). The duplicate detection still fires; the
        // error message lists the region only once thanks to the
        // .Distinct() in the validator's regions-collected formatter.
        var backend = MakeFake("vest", zones: ["pectoral_l"]);

        var options = new BodyMapOptions
        {
            Placements =
            {
                new Placement
                {
                    BackendId = "vest",
                    ZoneIds = { "pectoral_l" },
                    Region = BodyRegion.LeftUpperArm,
                },
                new Placement
                {
                    BackendId = "vest",
                    ZoneIds = { "pectoral_l" },
                    Region = BodyRegion.LeftUpperArm,
                },
            },
        };

        var result = new BodyMapValidator().Validate(new[] { backend }, options);

        var dup = result.Errors.Should().ContainSingle(
            e => e.Kind == BodyMapErrorKind.DuplicateZonePlacement).Subject;
        Regex.Matches(dup.Message, "LeftUpperArm").Count.Should().Be(1);
    }

    private static FakeBackend MakeFake(
        string id,
        IReadOnlyList<string>? zones = null,
        BodyRegion? forbidden = null)
    {
        var topology = new ZoneTopology();
        foreach (var z in zones ?? Array.Empty<string>())
        {
            topology.Zones.Add(new Zone { Id = z, DisplayName = z });
        }
        return new FakeBackend(id)
        {
            Zones = topology,
            ForbiddenRegions = forbidden is null
                ? ImmutableHashSet<BodyRegion>.Empty
                : ImmutableHashSet.Create(forbidden.Value),
        };
    }
}

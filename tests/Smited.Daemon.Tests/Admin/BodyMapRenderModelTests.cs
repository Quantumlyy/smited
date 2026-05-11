using FluentAssertions;
using Smited.Daemon.Admin.Components.Pages;
using Smited.Daemon.Admin.Services;
using Smited.Daemon.Backends;
using Smited.Daemon.Tests.Fixtures;
using Smited.V1;
using Xunit;

namespace Smited.Daemon.Tests.Admin;

public class BodyMapRenderModelTests
{
    private static FakeBackend MakeBackend(
        string id,
        string kind,
        params (string Id, float X, float Y, float Z, string Frame)[] zones)
    {
        var topo = new ZoneTopology();
        foreach (var (zid, x, y, z, frame) in zones)
        {
            topo.Zones.Add(new Zone
            {
                Id = zid,
                DisplayName = zid,
                Position = new PositionHint { X = x, Y = y, Z = z, Frame = frame },
            });
        }
        return new FakeBackend(id: id, kind: kind) { Zones = topo };
    }

    private static readonly DateTimeOffset _now =
        new(2026, 5, 11, 12, 0, 0, TimeSpan.Zero);

    private static IReadOnlyDictionary<(string, string), ZoneActivity> NoActivity() =>
        new Dictionary<(string, string), ZoneActivity>();

    /// <summary>
    /// PiShock advertises its zone with <c>Frame="device"</c> at
    /// (0.5, 0.5, 0.5) — a device-local placeholder, not body
    /// coordinates. Projecting that as body coords would draw every
    /// PiShock dot in the torso center regardless of where the operator
    /// placed the device. The body map is for body-frame zones only.
    /// </summary>
    [Fact]
    public void Device_frame_zones_are_skipped()
    {
        var pishock = MakeBackend("pishock-test", "pishock",
            ("op", 0.5f, 0.5f, 0.5f, "device"));

        var (front, back) = BodyMapRenderModel.Build(
            backends: new[] { pishock },
            activity: NoActivity(),
            bodyMap: null,
            now: _now);

        front.Should().BeEmpty("device-frame zones must not appear on the body silhouette");
        back.Should().BeEmpty();
    }

    [Fact]
    public void Body_frame_zones_render_on_the_correct_pane()
    {
        var owo = MakeBackend("mock-owo", "owo_skin",
            ("pectoral_l", 0.4f, 0.7f, 0.3f, "body"),       // front
            ("dorsal_l", 0.4f, 0.7f, 0.7f, "body"),         // back
            ("arm_l", 0.2f, 0.6f, 0.5f, "body"));           // front (Z=0.5 → front by convention)

        var (front, back) = BodyMapRenderModel.Build(
            backends: new[] { owo },
            activity: NoActivity(),
            bodyMap: null,
            now: _now);

        front.Select(z => z.ZoneId).Should().BeEquivalentTo("pectoral_l", "arm_l");
        back.Select(z => z.ZoneId).Should().BeEquivalentTo("dorsal_l");
    }

    [Fact]
    public void Zones_with_null_position_are_skipped()
    {
        var topo = new ZoneTopology();
        topo.Zones.Add(new Zone { Id = "no-pos", DisplayName = "Missing position" });
        var backend = new FakeBackend(id: "fake") { Zones = topo };

        var (front, back) = BodyMapRenderModel.Build(
            backends: new[] { backend },
            activity: NoActivity(),
            bodyMap: null,
            now: _now);

        front.Should().BeEmpty();
        back.Should().BeEmpty();
    }

    [Fact]
    public void BackendFilter_excludes_other_backends()
    {
        var owo = MakeBackend("mock-owo", "owo_skin", ("pectoral_l", 0.4f, 0.7f, 0.3f, "body"));
        var vest = MakeBackend("mock-vest", "bhaptics_vest", ("vest_front_l", 0.4f, 0.5f, 0.3f, "body"));

        var (front, _) = BodyMapRenderModel.Build(
            backends: new[] { owo, vest },
            activity: NoActivity(),
            bodyMap: null,
            now: _now,
            backendFilter: "mock-owo");

        front.Select(z => z.BackendId).Should().Equal("mock-owo");
    }

    /// <summary>
    /// Pre-fix bug: HeatLevel was baked into the RenderedZone at
    /// state-change time and never updated, so the documented 3-second
    /// fade never actually advanced — the page kept painting the same
    /// opacity until another event arrived. Recomputing per render
    /// (which the 200 ms fade ticker now does via RebuildZones) fixes
    /// this; the math itself lives here in BodyMapRenderModel.
    /// </summary>
    [Fact]
    public void HeatLevel_decays_linearly_from_LastFiredAt_over_fade_window()
    {
        var owo = MakeBackend("mock-owo", "owo_skin", ("pectoral_l", 0.4f, 0.7f, 0.3f, "body"));
        var firedAt = _now;
        // Completed sensation: IsActive=false, but the zone keeps its
        // LastFiredAt for the fade.
        var activity = new Dictionary<(string, string), ZoneActivity>
        {
            [("mock-owo", "pectoral_l")] = new(
                IsActive: false,
                LastFiredAt: firedAt,
                LastIntensity: 60u,
                ActiveSensationId: ""),
        };

        // t=0 (just completed) → full heat
        var (f0, _) = BodyMapRenderModel.Build(new[] { owo }, activity, null, firedAt);
        f0.Single().HeatLevel.Should().BeApproximately(1.0, 0.001);

        // t=1.5s (halfway through 3s window) → half heat
        var (f1, _) = BodyMapRenderModel.Build(new[] { owo }, activity, null, firedAt + TimeSpan.FromSeconds(1.5));
        f1.Single().HeatLevel.Should().BeApproximately(0.5, 0.001);

        // t=3s (end of window) → zero heat
        var (f2, _) = BodyMapRenderModel.Build(new[] { owo }, activity, null, firedAt + TimeSpan.FromSeconds(3));
        f2.Single().HeatLevel.Should().Be(0);

        // Past the window — stays at zero.
        var (f3, _) = BodyMapRenderModel.Build(new[] { owo }, activity, null, firedAt + TimeSpan.FromSeconds(10));
        f3.Single().HeatLevel.Should().Be(0);
    }

    [Fact]
    public void Active_zones_render_at_full_heat_regardless_of_age()
    {
        var owo = MakeBackend("mock-owo", "owo_skin", ("pectoral_l", 0.4f, 0.7f, 0.3f, "body"));
        // Fired long ago but still active (a long sensation still
        // running) — must paint at full heat, not fade out while
        // active.
        var activity = new Dictionary<(string, string), ZoneActivity>
        {
            [("mock-owo", "pectoral_l")] = new(
                IsActive: true,
                LastFiredAt: _now - TimeSpan.FromSeconds(60),
                LastIntensity: 60u,
                ActiveSensationId: "s1"),
        };

        var (front, _) = BodyMapRenderModel.Build(new[] { owo }, activity, null, _now);
        front.Single().HeatLevel.Should().Be(1.0);
        front.Single().IsActive.Should().BeTrue();
    }

    [Fact]
    public void Empty_backend_set_returns_empty_lists()
    {
        var (front, back) = BodyMapRenderModel.Build(
            backends: Array.Empty<IHapticBackend>(),
            activity: NoActivity(),
            bodyMap: null,
            now: _now);

        front.Should().BeEmpty();
        back.Should().BeEmpty();
    }
}

using Smited.V1;

namespace Smited.Daemon.Backends;

/// <summary>
/// Builds the <see cref="ZoneTopology"/> each bHaptics backend kind
/// advertises. Cross-platform: pure data factories with no SDK
/// references, so the mock backends in <c>Smited.Daemon</c> (net9.0)
/// and the real backends in <c>Smited.Daemon.Bhaptics</c>
/// (net9.0-windows) share the exact same zone definitions.
/// </summary>
/// <remarks>
/// <para>
/// Each backend advertises a mix of:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Cross-backend portable zone IDs.</b> Vest mirrors OWO's torso
/// zones (<c>pectoral_l/r</c>, <c>abdominal_l/r</c>, <c>lumbar_l/r</c>,
/// <c>dorsal_l/r</c>); sleeves and feet mirror OWO's <c>arm_l/r</c>
/// and add <c>foot_l/r</c>. A sensation file that targets
/// <c>arm_l</c> works on the OWO Skin AND on the bHaptics TactSleeve
/// without modification.
/// </description></item>
/// <item><description>
/// <b>Device-specific finer zones</b> prefixed with <c>bhaptics_</c>
/// so they cannot collide with cross-backend names. Authors who want
/// to target a specific motor cluster (e.g. just the wrist on the
/// sleeve, or just the upper-back motors on the vest) use these.
/// </description></item>
/// </list>
/// </remarks>
public static class BhapticsZoneTopology
{
    /// <summary>
    /// Zone topology for <c>bhaptics_vest</c> (TactSuit X40, 40
    /// actuators across the torso: 20 front, 20 back).
    /// </summary>
    public static ZoneTopology BuildVest()
    {
        var t = new ZoneTopology();

        // Cross-backend portable zones (mirror OWO's torso topology).
        AddZone(t, "pectoral_l", "Left pectoral", 0.4f, 0.7f, 0.3f);
        AddZone(t, "pectoral_r", "Right pectoral", 0.6f, 0.7f, 0.3f);
        AddZone(t, "abdominal_l", "Left abdominal", 0.4f, 0.5f, 0.3f);
        AddZone(t, "abdominal_r", "Right abdominal", 0.6f, 0.5f, 0.3f);
        AddZone(t, "lumbar_l", "Left lumbar", 0.4f, 0.5f, 0.7f);
        AddZone(t, "lumbar_r", "Right lumbar", 0.6f, 0.5f, 0.7f);
        AddZone(t, "dorsal_l", "Left dorsal", 0.4f, 0.7f, 0.7f);
        AddZone(t, "dorsal_r", "Right dorsal", 0.6f, 0.7f, 0.7f);

        // Device-specific finer zones. The TactSuit's 4×5 motor grid
        // (per side) is denser than OWO's 8-zone torso, so authors who
        // want finer-than-quadrant control can address smaller bands.
        AddZone(t, "bhaptics_vest_chest_high_l", "Vest upper chest, left", 0.4f, 0.78f, 0.25f);
        AddZone(t, "bhaptics_vest_chest_high_r", "Vest upper chest, right", 0.6f, 0.78f, 0.25f);
        AddZone(t, "bhaptics_vest_back_high_l", "Vest upper back, left", 0.4f, 0.78f, 0.75f);
        AddZone(t, "bhaptics_vest_back_high_r", "Vest upper back, right", 0.6f, 0.78f, 0.75f);

        AddGroup(t, "torso", "Torso",
            "pectoral_l", "pectoral_r",
            "abdominal_l", "abdominal_r",
            "lumbar_l", "lumbar_r",
            "dorsal_l", "dorsal_r");
        AddGroup(t, "all", "All zones",
            "pectoral_l", "pectoral_r",
            "abdominal_l", "abdominal_r",
            "lumbar_l", "lumbar_r",
            "dorsal_l", "dorsal_r");
        return t;
    }

    /// <summary>
    /// Zone topology for <c>bhaptics_sleeve_l</c> or
    /// <c>bhaptics_sleeve_r</c> (TactSleeve, 6 actuators per arm
    /// running wrist→bicep).
    /// </summary>
    public static ZoneTopology BuildSleeve(bool isLeft)
    {
        var t = new ZoneTopology();
        var suffix = isLeft ? "_l" : "_r";
        var displaySide = isLeft ? "left" : "right";
        var x = isLeft ? 0.2f : 0.8f;

        AddZone(t, $"arm{suffix}", $"{Capitalize(displaySide)} arm", x, 0.6f, 0.5f);
        AddZone(t, $"bhaptics_sleeve_wrist{suffix}", $"Sleeve wrist ({displaySide})", x, 0.45f, 0.5f);
        AddZone(t, $"bhaptics_sleeve_forearm{suffix}", $"Sleeve forearm ({displaySide})", x, 0.55f, 0.5f);
        AddZone(t, $"bhaptics_sleeve_elbow{suffix}", $"Sleeve elbow ({displaySide})", x, 0.6f, 0.5f);
        AddZone(t, $"bhaptics_sleeve_bicep{suffix}", $"Sleeve bicep ({displaySide})", x, 0.7f, 0.5f);
        return t;
    }

    /// <summary>
    /// Zone topology for <c>bhaptics_feet_l</c> or <c>bhaptics_feet_r</c>
    /// (Tactosy for Feet, 3 actuators per foot).
    /// </summary>
    public static ZoneTopology BuildFeet(bool isLeft)
    {
        var t = new ZoneTopology();
        var suffix = isLeft ? "_l" : "_r";
        var displaySide = isLeft ? "left" : "right";
        var x = isLeft ? 0.4f : 0.6f;

        AddZone(t, $"foot{suffix}", $"{Capitalize(displaySide)} foot", x, 0.1f, 0.5f);
        AddZone(t, $"bhaptics_feet_heel{suffix}", $"Foot heel ({displaySide})", x, 0.1f, 0.6f);
        AddZone(t, $"bhaptics_feet_arch{suffix}", $"Foot arch ({displaySide})", x, 0.1f, 0.5f);
        AddZone(t, $"bhaptics_feet_toes{suffix}", $"Foot toes ({displaySide})", x, 0.1f, 0.4f);
        return t;
    }

    private static void AddZone(ZoneTopology t, string id, string display, float x, float y, float z)
    {
        t.Zones.Add(new Zone
        {
            Id = id,
            DisplayName = display,
            Position = new PositionHint { X = x, Y = y, Z = z, Frame = "body" },
        });
    }

    private static void AddGroup(ZoneTopology t, string id, string display, params string[] members)
    {
        var g = new ZoneGroup { Id = id, DisplayName = display };
        foreach (var m in members)
        {
            g.ZoneIds.Add(m);
        }
        t.Groups.Add(g);
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

using Smited.V1;

namespace Smited.Daemon.Backends;

/// <summary>
/// Canonical zone topology and parameter schema for the bHaptics TactSuit
/// device family. Shared between <c>MockBhapticsBackend</c> and the real
/// <c>BhapticsBackend</c> (Windows-only) so the mock and the production
/// path cannot drift on motor count, group composition, or parameter
/// ranges.
///
/// Lives in <c>Smited.Daemon.Abstractions</c> rather than the daemon host
/// because <c>Smited.Daemon.Bhaptics</c> is a sibling assembly that
/// references abstractions but never the daemon (the same acyclic shape
/// as <c>Smited.Daemon.Owo</c>).
/// </summary>
public static class BhapticsTopology
{
    /// <summary>
    /// Number of vest motors per half (front or back). Constant across
    /// the TactSuit X40, X16, and Air variants — Air just disables the
    /// non-existent motors at the Player level. The daemon advertises
    /// 40 vest zones for every TactSuit.
    /// </summary>
    public const int VestMotorsPerHalf = 20;

    private const int VestColumns = 4;
    private const int VestRows = 5;

    /// <summary>
    /// Zone id frame label for the front of the vest. Distinct from the
    /// back so clients can pick which half a zone targets without parsing
    /// the zone id string.
    /// </summary>
    public const string FrameBodyFront = "body_front";

    /// <summary>Zone id frame label for the back of the vest.</summary>
    public const string FrameBodyBack = "body_back";

    /// <summary>
    /// Build the bHaptics zone topology. When <paramref name="accessoriesPresent"/>
    /// is true, the topology also includes 12 glove motors (6 per hand)
    /// and 8 forearm motors (4 per sleeve), plus the <c>gloves</c> and
    /// <c>arms</c> groups. Face and shoes accessories are intentionally
    /// out of scope for v0.1.x.
    /// </summary>
    public static ZoneTopology BuildZones(bool accessoriesPresent)
    {
        var t = new ZoneTopology();

        for (var i = 0; i < VestMotorsPerHalf; i++)
        {
            AddVestZone(t, $"vest_front_{i}", $"Vest front {i}", FrameBodyFront, i);
        }
        for (var i = 0; i < VestMotorsPerHalf; i++)
        {
            AddVestZone(t, $"vest_back_{i}", $"Vest back {i}", FrameBodyBack, i);
        }

        if (accessoriesPresent)
        {
            AddAccessoryZones(t);
        }

        AddVestGroups(t);
        if (accessoriesPresent)
        {
            AddAccessoryGroups(t);
        }
        AddAllGroup(t, accessoriesPresent);
        return t;
    }

    /// <summary>
    /// Expand a list of zone or zone-group IDs into a deduplicated list
    /// of motor-zone IDs. Group IDs are looked up in
    /// <paramref name="topology"/>'s <see cref="ZoneTopology.Groups"/> and
    /// replaced with their members; motor IDs are passed through.
    /// Insertion order is preserved (first occurrence wins) and
    /// duplicates are dropped — sending the same motor twice in one
    /// frame is wasted protocol bytes and the Player would just take
    /// the latest anyway.
    ///
    /// The trigger coordinator validates that every input ID is either
    /// a known zone or a known group, so unknown IDs at this layer
    /// indicate a programming error rather than user input.
    /// </summary>
    public static IReadOnlyList<string> ExpandGroupZoneIds(
        ZoneTopology topology,
        IEnumerable<string> zoneOrGroupIds)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(zoneOrGroupIds);

        var groupLookup = topology.Groups.ToDictionary(
            g => g.Id,
            g => (IReadOnlyList<string>)g.ZoneIds,
            StringComparer.OrdinalIgnoreCase);

        var expanded = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in zoneOrGroupIds)
        {
            if (groupLookup.TryGetValue(id, out var members))
            {
                foreach (var m in members)
                {
                    if (seen.Add(m)) expanded.Add(m);
                }
            }
            else if (seen.Add(id))
            {
                expanded.Add(id);
            }
        }
        return expanded;
    }

    /// <summary>
    /// Build the parameter schema for bHaptics. Three parameters:
    /// <c>intensity</c> (0..100, %), <c>duration</c> (0..10s — matches
    /// bHaptics Player's per-effect cap), and an optional <c>frequency</c>
    /// (50..200 Hz, X-series only — older devices ignore it but the
    /// schema honours it).
    /// </summary>
    public static ParameterSchema BuildParameters()
    {
        var s = new ParameterSchema();
        s.Parameters.Add(new ParameterDef
        {
            Name = "intensity",
            Type = ParameterType.Number,
            Required = true,
            Min = 0,
            Max = 100,
            Unit = "%",
            Description = "Per-motor PWM intensity, modulated by the Player slider.",
        });
        s.Parameters.Add(new ParameterDef
        {
            Name = "duration",
            Type = ParameterType.Duration,
            Required = true,
            Min = 0,
            Max = 10,
            Description = "Active vibration length. 10s ceiling matches bHaptics Player's per-effect cap.",
        });
        s.Parameters.Add(new ParameterDef
        {
            Name = "frequency",
            Type = ParameterType.Number,
            Required = false,
            Min = 50,
            Max = 200,
            Unit = "Hz",
            Description = "Optional motor frequency. TactSuit X-series only; older devices ignore it.",
        });
        return s;
    }

    private static void AddVestZone(ZoneTopology t, string id, string display, string frame, int motorIndex)
    {
        // Layout follows bHaptics' canonical 4-column × 5-row grid.
        // column = (id mod 4), row = (id div 4). Position hints normalise
        // into the unit square for cross-backend clients that just want a
        // rough body-frame coordinate.
        var column = motorIndex % VestColumns;
        var row = motorIndex / VestColumns;
        t.Zones.Add(new Zone
        {
            Id = id,
            DisplayName = display,
            Position = new PositionHint
            {
                X = column / (float)(VestColumns - 1),
                Y = row / (float)(VestRows - 1),
                Z = 0f,
                Frame = frame,
            },
        });
    }

    private static void AddAccessoryZones(ZoneTopology t)
    {
        // Gloves: 6 motors per hand. Frame separates left from right.
        for (var i = 0; i < 6; i++)
        {
            AddSimpleZone(t, $"glove_l_{i}", $"Left glove {i}", "hand_l", i, max: 5);
            AddSimpleZone(t, $"glove_r_{i}", $"Right glove {i}", "hand_r", i, max: 5);
        }
        // Sleeves: 4 motors per forearm.
        for (var i = 0; i < 4; i++)
        {
            AddSimpleZone(t, $"arm_l_{i}", $"Left forearm {i}", "forearm_l", i, max: 3);
            AddSimpleZone(t, $"arm_r_{i}", $"Right forearm {i}", "forearm_r", i, max: 3);
        }
    }

    private static void AddSimpleZone(ZoneTopology t, string id, string display, string frame, int index, int max)
    {
        t.Zones.Add(new Zone
        {
            Id = id,
            DisplayName = display,
            Position = new PositionHint
            {
                X = max == 0 ? 0f : index / (float)max,
                Y = 0f,
                Z = 0f,
                Frame = frame,
            },
        });
    }

    private static void AddVestGroups(ZoneTopology t)
    {
        var front = Enumerable.Range(0, VestMotorsPerHalf).Select(i => $"vest_front_{i}").ToArray();
        var back = Enumerable.Range(0, VestMotorsPerHalf).Select(i => $"vest_back_{i}").ToArray();

        AddGroup(t, "front", "Front of vest", front);
        AddGroup(t, "back", "Back of vest", back);
        AddGroup(t, "front_chest", "Front chest", front[..8]);
        AddGroup(t, "back_shoulders", "Back shoulders", back[..4]);
        AddGroup(t, "torso", "Torso", front.Concat(back).ToArray());
    }

    private static void AddAccessoryGroups(ZoneTopology t)
    {
        var gloves = Enumerable.Range(0, 6)
            .SelectMany(i => new[] { $"glove_l_{i}", $"glove_r_{i}" }).ToArray();
        var arms = Enumerable.Range(0, 4)
            .SelectMany(i => new[] { $"arm_l_{i}", $"arm_r_{i}" }).ToArray();

        AddGroup(t, "gloves", "Both gloves", gloves);
        AddGroup(t, "arms", "Both forearms", arms);
    }

    private static void AddAllGroup(ZoneTopology t, bool accessoriesPresent)
    {
        var ids = new List<string>(60);
        for (var i = 0; i < VestMotorsPerHalf; i++) ids.Add($"vest_front_{i}");
        for (var i = 0; i < VestMotorsPerHalf; i++) ids.Add($"vest_back_{i}");
        if (accessoriesPresent)
        {
            for (var i = 0; i < 6; i++) { ids.Add($"glove_l_{i}"); ids.Add($"glove_r_{i}"); }
            for (var i = 0; i < 4; i++) { ids.Add($"arm_l_{i}"); ids.Add($"arm_r_{i}"); }
        }
        AddGroup(t, "all", "All zones", ids.ToArray());
    }

    private static void AddGroup(ZoneTopology t, string id, string display, string[] members)
    {
        var g = new ZoneGroup { Id = id, DisplayName = display };
        foreach (var m in members)
        {
            g.ZoneIds.Add(m);
        }
        t.Groups.Add(g);
    }
}

namespace Smited.Daemon.Backends;

/// <summary>
/// Static zone-id → motor-index mapping for every bHaptics device kind.
/// </summary>
/// <remarks>
/// <para>
/// These indices are the authoritative translation from smited's zone
/// vocabulary into the byte-array offsets the bHaptics Player expects
/// in <c>HapticPlayer.Submit</c>. They must be verified against the
/// physical hardware on each commit — silent miswiring lands a
/// sensation on a different body region without ever erroring at the
/// SDK level. See the commit message that introduced this file for
/// the firmware-version verification record.
/// </para>
/// <para>
/// Returns an empty array for unrecognized zone IDs so a stray
/// zone reference (after upstream validation has somehow passed) is
/// silently ignored at trigger time rather than throwing — same
/// failure-soft policy <c>OwoMuscleMap</c>'s callers use.
/// </para>
/// </remarks>
public static class BhapticsMotorMap
{
    /// <summary>
    /// TactSuit X40 layout: motors 0–19 are the 20 front actuators (4
    /// columns × 5 rows, top-left to bottom-right), motors 20–39 are
    /// the matching 20 back actuators. Cross-backend zones cover
    /// quadrants of the torso (every front and back motor is in
    /// exactly one of <c>pectoral_*/abdominal_*/dorsal_*/lumbar_*</c>);
    /// device-specific zones cover the narrower upper-chest / upper-back
    /// bands sensation authors most often want addressable.
    /// </summary>
    public static IReadOnlyList<int> VestMotorsForZone(string zoneId) => zoneId switch
    {
        // ---- Front (0..19), 4 columns × 5 rows ----
        // Row 0 (top): 0 1 2 3 — chest, upper line
        // Row 1:       4 5 6 7 — chest, lower line
        // Row 2:       8 9 10 11 — ribs
        // Row 3:       12 13 14 15 — upper abdomen
        // Row 4 (bot): 16 17 18 19 — lower abdomen
        "pectoral_l" => [0, 1, 4, 5],
        "pectoral_r" => [2, 3, 6, 7],
        "abdominal_l" => [8, 9, 12, 13, 16, 17],
        "abdominal_r" => [10, 11, 14, 15, 18, 19],

        // ---- Back (20..39), same 4×5 layout mirrored ----
        // Row 0 (top): 20 21 22 23 — upper back / shoulder blades
        // Row 1:       24 25 26 27 — mid back
        // Row 2:       28 29 30 31 — lower mid back
        // Row 3:       32 33 34 35 — upper lumbar
        // Row 4 (bot): 36 37 38 39 — lower lumbar
        "dorsal_l" => [20, 21, 24, 25],
        "dorsal_r" => [22, 23, 26, 27],
        "lumbar_l" => [28, 29, 32, 33, 36, 37],
        "lumbar_r" => [30, 31, 34, 35, 38, 39],

        // ---- Device-specific finer zones ----
        "bhaptics_vest_chest_high_l" => [0, 1],
        "bhaptics_vest_chest_high_r" => [2, 3],
        "bhaptics_vest_back_high_l" => [20, 21],
        "bhaptics_vest_back_high_r" => [22, 23],

        _ => Array.Empty<int>(),
    };

    /// <summary>
    /// TactSleeve layout: 6 actuators per arm, ordered wrist (0) →
    /// forearm-distal (1) → forearm-proximal (2) → elbow (3) →
    /// bicep-distal (4) → bicep-proximal (5). The
    /// <paramref name="isLeft"/> flag selects which side's zone ID
    /// suffix (<c>_l</c> vs <c>_r</c>) this map answers; only the
    /// matching-side backend should ever pass non-matching IDs.
    /// </summary>
    public static IReadOnlyList<int> SleeveMotorsForZone(string zoneId, bool isLeft)
    {
        var suffix = isLeft ? "_l" : "_r";
        if (zoneId == $"arm{suffix}")
        {
            return [0, 1, 2, 3, 4, 5];
        }
        if (zoneId == $"bhaptics_sleeve_wrist{suffix}")
        {
            return [0];
        }
        if (zoneId == $"bhaptics_sleeve_forearm{suffix}")
        {
            return [1, 2];
        }
        if (zoneId == $"bhaptics_sleeve_elbow{suffix}")
        {
            return [3];
        }
        if (zoneId == $"bhaptics_sleeve_bicep{suffix}")
        {
            return [4, 5];
        }
        return Array.Empty<int>();
    }

    /// <summary>
    /// Tactosy for Feet layout: 3 actuators per foot, ordered heel
    /// (0) → arch (1) → toes (2).
    /// </summary>
    public static IReadOnlyList<int> FeetMotorsForZone(string zoneId, bool isLeft)
    {
        var suffix = isLeft ? "_l" : "_r";
        if (zoneId == $"foot{suffix}")
        {
            return [0, 1, 2];
        }
        if (zoneId == $"bhaptics_feet_heel{suffix}")
        {
            return [0];
        }
        if (zoneId == $"bhaptics_feet_arch{suffix}")
        {
            return [1];
        }
        if (zoneId == $"bhaptics_feet_toes{suffix}")
        {
            return [2];
        }
        return Array.Empty<int>();
    }
}

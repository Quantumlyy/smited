// Excluded from compile on non-Windows hosts; references the OWO SDK's
// Muscle enum which only exists in the Windows-only NuGet package.

#if WINDOWS
using OWOGame;

namespace Smited.Daemon.Owo;

/// <summary>
/// Translates smited's zone IDs (the strings exposed in
/// <see cref="OwoBackend.Zones"/>) to the OWO SDK's <c>Muscle</c> enum
/// values. The set of zone IDs is fixed and matches
/// <c>MockOwoBackend</c>'s topology so an authored sensation library is
/// portable between the mock and real backends without re-mapping.
/// </summary>
internal static class OwoMuscleMap
{
    private static readonly IReadOnlyDictionary<string, Muscle> Map =
        new Dictionary<string, Muscle>(StringComparer.OrdinalIgnoreCase)
        {
            ["pectoral_l"] = Muscle.Pectoral_L,
            ["pectoral_r"] = Muscle.Pectoral_R,
            ["abdominal_l"] = Muscle.Abdominal_L,
            ["abdominal_r"] = Muscle.Abdominal_R,
            ["lumbar_l"] = Muscle.Lumbar_L,
            ["lumbar_r"] = Muscle.Lumbar_R,
            ["dorsal_l"] = Muscle.Dorsal_L,
            ["dorsal_r"] = Muscle.Dorsal_R,
            ["arm_l"] = Muscle.Arm_L,
            ["arm_r"] = Muscle.Arm_R,
        };

    /// <summary>
    /// Resolve a single zone id to its <see cref="Muscle"/>. Throws when
    /// the id is unknown — the daemon's validation layer should reject
    /// triggers with unknown zones long before they reach this point, so
    /// arriving here means an invariant violation rather than user error.
    /// </summary>
    public static Muscle Resolve(string zoneId)
    {
        if (Map.TryGetValue(zoneId, out var muscle))
        {
            return muscle;
        }

        throw new InvalidOperationException(
            $"Zone '{zoneId}' is not a valid OWO muscle zone. "
            + "The daemon's validation layer should have rejected this earlier; "
            + "if you're seeing this, the trigger flow bypassed zone validation.");
    }

    /// <summary>
    /// Resolve a sequence of zone ids in order. Convenience for the
    /// trigger path which needs <c>Muscle[]</c> for
    /// <c>Sensation.WithMuscles</c>.
    /// </summary>
    public static Muscle[] Resolve(IReadOnlyList<string> zoneIds)
    {
        var result = new Muscle[zoneIds.Count];
        for (var i = 0; i < zoneIds.Count; i++)
        {
            result[i] = Resolve(zoneIds[i]);
        }
        return result;
    }
}
#endif

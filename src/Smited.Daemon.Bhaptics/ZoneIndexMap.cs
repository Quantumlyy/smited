using Smited.Daemon.Bhaptics.WebSocket;

namespace Smited.Daemon.Bhaptics;

/// <summary>
/// Maps smited zone IDs to bHaptics motor indices and Position enum
/// values. The mapping is constant per device family and derived from
/// bHaptics' published motor layout (the Designer tool's grid view).
///
/// Internal because nothing outside <c>Smited.Daemon.Bhaptics</c> should
/// reference this directly; the daemon talks to backends only via
/// <c>IHapticBackend</c>.
/// </summary>
internal static class ZoneIndexMap
{
    private static readonly Dictionary<string, (Position position, int motorIndex)> Table = BuildTable();

    /// <summary>
    /// Resolve a zone id like <c>"vest_front_7"</c> to
    /// <c>(Position.VestFront, motorIndex: 7)</c>. Throws
    /// <see cref="ArgumentException"/> if the zone id doesn't belong to
    /// this backend's topology — the daemon's validation layer should
    /// have rejected the trigger before reaching this point, so seeing
    /// an unknown zone here indicates a programming error.
    /// </summary>
    public static (Position position, int motorIndex) Resolve(string zoneId)
    {
        if (Table.TryGetValue(zoneId, out var entry))
        {
            return entry;
        }
        throw new ArgumentException($"Unknown bHaptics zone id '{zoneId}'.", nameof(zoneId));
    }

    /// <summary>
    /// For a set of zone IDs, returns the smallest <see cref="Position"/>
    /// that encloses them all. Used to pick the right top-level position
    /// when submitting a multi-zone pattern: a request that targets only
    /// front motors uses <see cref="Position.VestFront"/>, only back
    /// motors uses <see cref="Position.VestBack"/>, and a mix uses the
    /// full <see cref="Position.Vest"/>.
    /// </summary>
    public static Position EnclosingPosition(IEnumerable<string> zoneIds)
    {
        ArgumentNullException.ThrowIfNull(zoneIds);

        Position? result = null;
        foreach (var id in zoneIds)
        {
            var (position, _) = Resolve(id);
            if (result is null)
            {
                result = position;
                continue;
            }

            if (result == position) continue;

            // Front + back of the vest collapse to the full Vest position.
            if ((result is Position.VestFront && position is Position.VestBack) ||
                (result is Position.VestBack && position is Position.VestFront) ||
                result is Position.Vest || position is Position.Vest)
            {
                result = Position.Vest;
                continue;
            }

            // Mixed accessory positions (e.g. glove + sleeve) aren't a
            // single bHaptics position — the caller should have split
            // these into separate submits. Surface as ArgumentException
            // so the bug is loud rather than silently picking one half.
            throw new ArgumentException(
                $"Zone ids span incompatible bHaptics positions ({result} and {position}); split into separate submits.",
                nameof(zoneIds));
        }

        if (result is null)
        {
            throw new ArgumentException("Cannot resolve enclosing Position from an empty zone id set.", nameof(zoneIds));
        }
        return result.Value;
    }

    private static Dictionary<string, (Position, int)> BuildTable()
    {
        var t = new Dictionary<string, (Position, int)>(StringComparer.Ordinal);
        for (var i = 0; i < 20; i++)
        {
            t[$"vest_front_{i}"] = (Position.VestFront, i);
            t[$"vest_back_{i}"] = (Position.VestBack, i);
        }
        for (var i = 0; i < 6; i++)
        {
            t[$"glove_l_{i}"] = (Position.GloveL, i);
            t[$"glove_r_{i}"] = (Position.GloveR, i);
        }
        for (var i = 0; i < 4; i++)
        {
            t[$"arm_l_{i}"] = (Position.ForearmL, i);
            t[$"arm_r_{i}"] = (Position.ForearmR, i);
        }
        return t;
    }
}

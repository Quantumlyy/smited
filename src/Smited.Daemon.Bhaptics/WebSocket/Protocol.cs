// bHaptics Player WebSocket v2 protocol DTOs.
//
// Field names below follow the haptic-library wiki conventions (camelCase
// over the wire). The JSON shape MUST be verified against a live wscat
// capture during on-hardware smoke testing — the daemon's tests use a
// Kestrel-backed simulator that mirrors what the spec says the Player
// accepts, but the simulator can't catch wire-format drift on its own.
// If a Player update changes the schema (or our reading of the wiki was
// wrong), update the records here and the JsonPropertyName attributes
// match what the live Player emits/accepts.
//
// See docs/bhaptics.md for the on-hardware verification checklist.

using System.Text.Json.Serialization;

namespace Smited.Daemon.Bhaptics.WebSocket;

/// <summary>
/// Top-level submit frame sent to <c>ws://localhost:15881/v2/feedbacks</c>.
/// Each frame can carry zero or more pre-authored event registrations
/// (<see cref="Register"/> — unused in smited's programmatic-only mode)
/// and zero or more programmatic submit entries (<see cref="Submit"/> —
/// what smited actually sends).
/// </summary>
internal sealed record SubmitFrame
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "frame";

    [JsonPropertyName("register")]
    public IReadOnlyList<RegisterPair>? Register { get; init; }

    [JsonPropertyName("submit")]
    public IReadOnlyList<SubmitEntry>? Submit { get; init; }
}

/// <summary>
/// Pre-authored event registration entry. Defined for completeness;
/// smited only uses programmatic dot-mode patterns and never populates
/// this list, so the type doesn't carry the full bHaptics event
/// metadata. If pre-authored event support is added later, expand this
/// record to match the Player's event registration shape.
/// </summary>
internal sealed record RegisterPair
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("project")]
    public string Project { get; init; } = string.Empty;
}

/// <summary>
/// One programmatic motor pattern. <see cref="Type"/> is <c>"dotMode"</c>
/// for per-motor intensity arrays or <c>"pathMode"</c> for coordinate
/// interpolation; smited only emits <c>dotMode</c> in v0.1.x.
/// </summary>
internal sealed record SubmitEntry
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "dotMode";

    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("durationMillis")]
    public int DurationMillis { get; init; }

    [JsonPropertyName("frame")]
    public Frame Frame { get; init; } = new();
}

/// <summary>
/// Per-position frame payload. Carries either dot points (per-motor
/// intensity 0..100) or path points (continuous coordinate interpolation).
/// The Player picks based on the parent <see cref="SubmitEntry.Type"/>.
/// </summary>
internal sealed record Frame
{
    [JsonPropertyName("position")]
    public Position Position { get; init; }

    [JsonPropertyName("dotPoints")]
    public IReadOnlyList<DotPoint>? DotPoints { get; init; }

    [JsonPropertyName("pathPoints")]
    public IReadOnlyList<PathPoint>? PathPoints { get; init; }

    [JsonPropertyName("durationMillis")]
    public int DurationMillis { get; init; }
}

/// <summary>
/// Per-motor intensity entry in a dot-mode frame. Index is the motor
/// position within the device family's canonical layout (0..19 for
/// vest halves, 0..5 for gloves, 0..3 for sleeves). Intensity is 0..100.
/// </summary>
internal sealed record DotPoint(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("intensity")] int Intensity);

/// <summary>
/// Coordinate point in a path-mode frame. Reserved for future use —
/// smited's v0.1.x doesn't emit path-mode patterns, so this record
/// only exists to keep the protocol surface complete.
/// </summary>
internal sealed record PathPoint(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("intensity")] int Intensity);

/// <summary>
/// Device family target for a <see cref="Frame"/>. Integer values match
/// the bHaptics Player wire protocol (verified against the haptic-library
/// wiki documentation). <see cref="Vest"/> covers both halves of a
/// TactSuit; <see cref="VestFront"/> / <see cref="VestBack"/> target a
/// single half. Per-accessory positions exist for the gloves, sleeves,
/// head and shoes families.
/// </summary>
internal enum Position
{
    /// <summary>Both front and back halves of a TactSuit.</summary>
    Vest = 0,

    /// <summary>Front half of a TactSuit only.</summary>
    VestFront = 201,

    /// <summary>Back half of a TactSuit only.</summary>
    VestBack = 202,

    /// <summary>Left forearm sleeve.</summary>
    ForearmL = 3,

    /// <summary>Right forearm sleeve.</summary>
    ForearmR = 4,

    /// <summary>Head accessory.</summary>
    Head = 1,

    /// <summary>Left hand (non-glove).</summary>
    HandL = 5,

    /// <summary>Right hand (non-glove).</summary>
    HandR = 6,

    /// <summary>Left foot.</summary>
    FootL = 7,

    /// <summary>Right foot.</summary>
    FootR = 8,

    /// <summary>Left TactGlove.</summary>
    GloveL = 11,

    /// <summary>Right TactGlove.</summary>
    GloveR = 12,
}

/// <summary>
/// Inbound device-status frame the Player pushes to the daemon. Fired
/// on connect, on device pairing change, and (per the haptic-library
/// docs) periodically as a heartbeat. The daemon mirrors this into the
/// backend's <c>Extras</c> field for client visibility.
/// </summary>
internal sealed record StatusFrame
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "deviceStatus";

    [JsonPropertyName("devices")]
    public IReadOnlyList<DeviceStatus>? Devices { get; init; }
}

/// <summary>
/// One device's connection state and battery percentage. Position
/// identifies which family the device fills.
/// </summary>
internal sealed record DeviceStatus
{
    [JsonPropertyName("position")]
    public Position Position { get; init; }

    [JsonPropertyName("connected")]
    public bool Connected { get; init; }

    [JsonPropertyName("batteryPercent")]
    public int BatteryPercent { get; init; }
}

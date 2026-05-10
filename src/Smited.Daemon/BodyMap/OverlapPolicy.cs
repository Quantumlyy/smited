namespace Smited.Daemon.BodyMap;

/// <summary>
/// What happens when two registered backends declare placements that
/// occupy the same body region.
/// </summary>
public enum OverlapPolicy
{
    /// <summary>
    /// Default. Startup logs a warning naming the overlap; triggers
    /// proceed normally.
    /// </summary>
    Warn = 0,

    /// <summary>
    /// Startup logs the same warning, AND <c>TriggerCoordinator</c>
    /// rejects any trigger whose resolved zone-set crosses an overlap
    /// region with <c>INVALID_ZONE</c>. Use when two backends could
    /// physically interfere.
    /// </summary>
    Refuse,

    /// <summary>
    /// No warning, no rejection. Use when overlapping coverage is
    /// intentional (e.g. layering haptic categories).
    /// </summary>
    Off,
}

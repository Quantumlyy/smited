using Smited.Daemon.Backends.Internal;

namespace Smited.Daemon.Sensations;

/// <summary>
/// Library entry stored in <see cref="SensationLibrary"/>. Mirrors the proto
/// <c>RegisteredSensation</c> in shape but uses internal domain types so
/// backends and the trigger coordinator don't depend on generated wire types.
/// </summary>
public sealed record RegisteredSensation(
    string Name,
    string BackendId,
    string DisplayName,
    string Description,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> DefaultZoneIds,
    uint? DefaultIntensity,
    TimeSpan EstimatedDuration,
    DateTimeOffset RegisteredAt,
    IReadOnlyList<MicrosensationParameters> Definition);

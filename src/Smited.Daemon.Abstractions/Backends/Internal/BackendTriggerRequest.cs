namespace Smited.Daemon.Backends.Internal;

/// <summary>
/// Trigger payload handed to a backend after the coordinator has resolved
/// the sensation, validated parameters and zones, and assigned a runtime
/// id. Backends never see the proto request directly.
/// </summary>
public sealed record BackendTriggerRequest(
    string SensationId,
    string? SensationName,
    IReadOnlyList<string> ZoneIds,
    uint? IntensityScale,
    int Priority,
    string ClientTraceId,
    IReadOnlyList<MicrosensationParameters> Microsensations);

/// <summary>
/// Parameter values for one microsensation, keyed by ParameterDef.Name.
/// </summary>
public sealed record MicrosensationParameters(
    IReadOnlyDictionary<string, ParameterValue> Values);

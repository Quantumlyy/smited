using Smited.Daemon.Backends;
using Smited.V1;
using DomainParameterValue = Smited.Daemon.Backends.Internal.ParameterValue;
using DomainMicrosensationParameters = Smited.Daemon.Backends.Internal.MicrosensationParameters;

namespace Smited.Daemon.Sensations;

/// <summary>
/// Validates a sensation against a specific backend's parameter schema
/// and zone topology. Used by both the boot-time
/// <see cref="SensationLoader"/> (which wraps any failure in a
/// <see cref="SmitedStartupException"/>) and the runtime
/// <c>RegisterSensation</c> RPC handler (which converts failures into a
/// structured <c>RegisterSensationResponse{Registered=false, Error=...}</c>
/// rather than throwing).
/// </summary>
internal static class SensationValidator
{
    /// <summary>
    /// Returns <c>null</c> if the sensation is valid for the backend;
    /// otherwise returns a <c>(field, message)</c> tuple describing the
    /// first failure encountered. Field paths use the proto convention
    /// (<c>microsensations[N].parameters.{name}</c>, <c>default_zone_ids</c>).
    /// </summary>
    public static (string Field, string Message)? Validate(
        IReadOnlyList<DomainMicrosensationParameters> microsensations,
        IReadOnlyList<string> defaultZoneIds,
        IHapticBackend backend)
    {
        ArgumentNullException.ThrowIfNull(microsensations);
        ArgumentNullException.ThrowIfNull(defaultZoneIds);
        ArgumentNullException.ThrowIfNull(backend);

        var paramByName = backend.Parameters.Parameters
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < microsensations.Count; i++)
        {
            var micro = microsensations[i];
            var presentKeys = new HashSet<string>(micro.Values.Keys, StringComparer.OrdinalIgnoreCase);

            foreach (var (key, value) in micro.Values)
            {
                if (!paramByName.TryGetValue(key, out var def))
                {
                    return ($"microsensations[{i}].parameters.{key}",
                        $"parameter '{key}' is not declared by backend '{backend.Id}'");
                }
                if (!ValueMatchesType(value, def))
                {
                    return ($"microsensations[{i}].parameters.{key}",
                        $"parameter '{key}' has wrong value type for declared {def.Type}");
                }
                if (!ValueWithinRange(value, def, out var rangeError))
                {
                    return ($"microsensations[{i}].parameters.{key}",
                        $"parameter '{key}' out of range: {rangeError}");
                }
            }

            foreach (var def in backend.Parameters.Parameters)
            {
                if (def.Required && !presentKeys.Contains(def.Name))
                {
                    return ($"microsensations[{i}].parameters.{def.Name}",
                        $"required parameter '{def.Name}' is missing");
                }
            }
        }

        var knownZones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var z in backend.Zones.Zones) knownZones.Add(z.Id);
        foreach (var g in backend.Zones.Groups) knownZones.Add(g.Id);

        foreach (var zone in defaultZoneIds)
        {
            if (!knownZones.Contains(zone))
            {
                return ("default_zone_ids",
                    $"zone '{zone}' is not present on backend '{backend.Id}'");
            }
        }

        return null;
    }

    private static bool ValueMatchesType(DomainParameterValue value, ParameterDef def) => def.Type switch
    {
        ParameterType.Number => value is DomainParameterValue.Number,
        ParameterType.Bool => value is DomainParameterValue.Bool,
        ParameterType.String => value is DomainParameterValue.Text,
        ParameterType.Duration => value is DomainParameterValue.Duration,
        ParameterType.Enum => value is DomainParameterValue.EnumValue,
        _ => false,
    };

    private static bool ValueWithinRange(DomainParameterValue value, ParameterDef def, out string? error)
    {
        error = null;
        switch (value)
        {
            case DomainParameterValue.Number n:
                if (def.HasMin && n.Value < def.Min) { error = $"{n.Value} < min {def.Min}"; return false; }
                if (def.HasMax && n.Value > def.Max) { error = $"{n.Value} > max {def.Max}"; return false; }
                break;
            case DomainParameterValue.Duration d:
                var seconds = d.Value.TotalSeconds;
                if (def.HasMin && seconds < def.Min) { error = $"{seconds}s < min {def.Min}s"; return false; }
                if (def.HasMax && seconds > def.Max) { error = $"{seconds}s > max {def.Max}s"; return false; }
                break;
            case DomainParameterValue.EnumValue e:
                if (def.EnumValues.Count > 0 && !def.EnumValues.Contains(e.Value))
                {
                    error = $"'{e.Value}' is not in enum_values [{string.Join(", ", def.EnumValues)}]";
                    return false;
                }
                break;
        }
        return true;
    }
}

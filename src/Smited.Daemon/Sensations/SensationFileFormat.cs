using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Smited.Daemon.Backends.Internal;

namespace Smited.Daemon.Sensations;

/// <summary>
/// JSON DTO mirroring the spec's snake_case sensation file format:
/// <code>
/// {
///   "name": "compile_error_severe",
///   "backend_kind": "owo_skin",
///   "display_name": "Compile Error (Severe)",
///   "description": "...",
///   "tags": ["build", "error", "severe"],
///   "default_zone_ids": ["pectoral_l", "pectoral_r"],
///   "default_intensity": 80,
///   "estimated_duration": "0.6s",
///   "definition": {
///     "microsensations": [
///       { "parameters": { "frequency": { "number": 100 }, ... } }
///     ]
///   }
/// }
/// </code>
/// </summary>
public sealed class SensationFileDto
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    [JsonPropertyName("backend_kind")] public string BackendKind { get; set; } = "";

    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = "";

    [JsonPropertyName("description")] public string Description { get; set; } = "";

    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();

    [JsonPropertyName("default_zone_ids")] public List<string> DefaultZoneIds { get; set; } = new();

    [JsonPropertyName("default_intensity")] public uint? DefaultIntensity { get; set; }

    [JsonPropertyName("estimated_duration")]
    [JsonConverter(typeof(DurationStringConverter))]
    public TimeSpan EstimatedDuration { get; set; }

    [JsonPropertyName("definition")] public InlineSensationDto Definition { get; set; } = new();
}

public sealed class InlineSensationDto
{
    [JsonPropertyName("microsensations")]
    public List<MicrosensationDto> Microsensations { get; set; } = new();
}

public sealed class MicrosensationDto
{
    [JsonPropertyName("parameters")]
    public Dictionary<string, ParameterValue> Parameters { get; set; } = new();
}

/// <summary>
/// Reads the proto <c>ParameterValue</c> oneof shape directly into the
/// internal <see cref="ParameterValue"/> hierarchy. Exactly one of
/// <c>number</c>/<c>bool_value</c>/<c>string_value</c>/<c>duration</c>/
/// <c>enum_value</c> must be present; ambiguous shapes are rejected.
/// </summary>
public sealed class ParameterValueJsonConverter : JsonConverter<ParameterValue>
{
    public override ParameterValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected an object for ParameterValue");
        }

        ParameterValue? result = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (result is null)
                {
                    throw new JsonException("ParameterValue must have exactly one of number/bool_value/string_value/duration/enum_value");
                }
                return result;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected property name in ParameterValue");
            }

            var propName = reader.GetString()!;
            reader.Read();

            if (result is not null)
            {
                throw new JsonException($"ParameterValue has multiple oneof variants set; saw '{propName}' after another");
            }

            result = propName switch
            {
                "number" => new ParameterValue.Number(reader.GetDouble()),
                "bool_value" => new ParameterValue.Bool(reader.GetBoolean()),
                "string_value" => new ParameterValue.Text(reader.GetString()!),
                "duration" => new ParameterValue.Duration(DurationStringConverter.Parse(reader.GetString()!)),
                "enum_value" => new ParameterValue.EnumValue(reader.GetString()!),
                _ => throw new JsonException($"Unknown ParameterValue oneof variant: '{propName}'"),
            };
        }

        throw new JsonException("Unterminated ParameterValue object");
    }

    public override void Write(Utf8JsonWriter writer, ParameterValue value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case ParameterValue.Number n:
                writer.WriteNumber("number", n.Value);
                break;
            case ParameterValue.Bool b:
                writer.WriteBoolean("bool_value", b.Value);
                break;
            case ParameterValue.Text t:
                writer.WriteString("string_value", t.Value);
                break;
            case ParameterValue.Duration d:
                writer.WriteString("duration", DurationStringConverter.Format(d.Value));
                break;
            case ParameterValue.EnumValue e:
                writer.WriteString("enum_value", e.Value);
                break;
        }
        writer.WriteEndObject();
    }
}

/// <summary>
/// Parses duration strings shaped like <c>"0.4s"</c> or <c>"40ms"</c>.
/// </summary>
public sealed class DurationStringConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Parse(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Format(value));

    public static TimeSpan Parse(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            throw new JsonException("Empty duration string");
        }

        if (raw.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromMilliseconds(double.Parse(raw[..^2], CultureInfo.InvariantCulture));
        }
        if (raw.EndsWith('s'))
        {
            return TimeSpan.FromSeconds(double.Parse(raw[..^1], CultureInfo.InvariantCulture));
        }
        throw new JsonException($"Unrecognised duration format '{raw}' (expected '<n>s' or '<n>ms')");
    }

    public static string Format(TimeSpan value)
    {
        if (value.TotalMilliseconds < 1000 && value.TotalMilliseconds == Math.Truncate(value.TotalMilliseconds))
        {
            return $"{value.TotalMilliseconds:0}ms";
        }
        return $"{value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s";
    }
}

public static class SensationFileSerializer
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters =
        {
            new ParameterValueJsonConverter(),
        },
    };

    public static SensationFileDto Deserialize(string json) =>
        JsonSerializer.Deserialize<SensationFileDto>(json, Options)
            ?? throw new JsonException("Top-level JSON object was null");

    public static string Serialize(SensationFileDto file) =>
        JsonSerializer.Serialize(file, Options);

    /// <summary>
    /// Resolve a parsed file to an internal <see cref="RegisteredSensation"/>
    /// for one specific backend (binding the file's <c>backend_kind</c> to a
    /// concrete <paramref name="backendId"/>). The <paramref name="now"/>
    /// stamp is used for <c>RegisteredAt</c>.
    /// </summary>
    public static RegisteredSensation ToInternal(
        SensationFileDto file,
        string backendId,
        DateTimeOffset now)
    {
        var definition = file.Definition.Microsensations
            .Select(m => new MicrosensationParameters(m.Parameters))
            .ToArray();

        return new RegisteredSensation(
            Name: file.Name,
            BackendId: backendId,
            DisplayName: file.DisplayName,
            Description: file.Description,
            Tags: file.Tags.ToArray(),
            DefaultZoneIds: file.DefaultZoneIds.ToArray(),
            DefaultIntensity: file.DefaultIntensity,
            EstimatedDuration: file.EstimatedDuration,
            RegisteredAt: now,
            Definition: definition);
    }

    /// <summary>
    /// Serialises a <see cref="RegisteredSensation"/> back to its on-disk
    /// <see cref="SensationFileDto"/> shape, suitable for writing into the
    /// sensation library directory tree. A round-trip through this method
    /// and <see cref="ToInternal(SensationFileDto, string, DateTimeOffset)"/>
    /// yields the same record, modulo <c>RegisteredAt</c> precision (the
    /// timestamp is not stored in the on-disk format).
    /// </summary>
    /// <param name="sensation">The library entry to serialise.</param>
    /// <param name="backendKind">
    /// The hardware family this file binds to. Required because the
    /// internal record carries only <c>BackendId</c> (a runtime id);
    /// <c>backend_kind</c> is the file-level binding the loader uses.
    /// </param>
    public static SensationFileDto ToDto(RegisteredSensation sensation, string backendKind)
    {
        ArgumentNullException.ThrowIfNull(sensation);
        ArgumentException.ThrowIfNullOrEmpty(backendKind);

        return new SensationFileDto
        {
            Name = sensation.Name,
            BackendKind = backendKind,
            DisplayName = sensation.DisplayName,
            Description = sensation.Description,
            Tags = sensation.Tags.ToList(),
            DefaultZoneIds = sensation.DefaultZoneIds.ToList(),
            DefaultIntensity = sensation.DefaultIntensity,
            EstimatedDuration = sensation.EstimatedDuration,
            Definition = new InlineSensationDto
            {
                Microsensations = sensation.Definition
                    .Select(m => new MicrosensationDto
                    {
                        Parameters = new Dictionary<string, ParameterValue>(m.Values, StringComparer.OrdinalIgnoreCase),
                    })
                    .ToList(),
            },
        };
    }
}

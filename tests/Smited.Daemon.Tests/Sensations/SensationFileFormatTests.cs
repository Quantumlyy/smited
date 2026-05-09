using System.Text.Json;
using FluentAssertions;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Sensations;
using Xunit;

namespace Smited.Daemon.Tests.Sensations;

public class SensationFileFormatTests
{
    private const string SpecExample = """
        {
          "name": "compile_error_severe",
          "backend_kind": "owo_skin",
          "display_name": "Compile Error (Severe)",
          "description": "Strong jab to both pectorals — many errors at once.",
          "tags": ["build", "error", "severe"],
          "default_zone_ids": ["pectoral_l", "pectoral_r"],
          "default_intensity": 80,
          "estimated_duration": "0.6s",
          "definition": {
            "microsensations": [
              {
                "parameters": {
                  "frequency": { "number": 100 },
                  "intensity": { "number": 80 },
                  "duration": { "duration": "0.4s" },
                  "ramp_up": { "duration": "0.05s" },
                  "ramp_down": { "duration": "0.15s" }
                }
              }
            ]
          }
        }
        """;

    [Fact]
    public void Spec_example_round_trips_through_DTO()
    {
        var dto = SensationFileSerializer.Deserialize(SpecExample);

        dto.Name.Should().Be("compile_error_severe");
        dto.BackendKind.Should().Be("owo_skin");
        dto.DisplayName.Should().Be("Compile Error (Severe)");
        dto.Tags.Should().BeEquivalentTo("build", "error", "severe");
        dto.DefaultZoneIds.Should().BeEquivalentTo("pectoral_l", "pectoral_r");
        dto.DefaultIntensity.Should().Be(80);
        dto.EstimatedDuration.Should().Be(TimeSpan.FromMilliseconds(600));

        var micro = dto.Definition.Microsensations.Should().ContainSingle().Subject;
        micro.Parameters["frequency"].Should().Be(new ParameterValue.Number(100));
        micro.Parameters["intensity"].Should().Be(new ParameterValue.Number(80));
        micro.Parameters["duration"].Should().Be(new ParameterValue.Duration(TimeSpan.FromMilliseconds(400)));
        micro.Parameters["ramp_up"].Should().Be(new ParameterValue.Duration(TimeSpan.FromMilliseconds(50)));
        micro.Parameters["ramp_down"].Should().Be(new ParameterValue.Duration(TimeSpan.FromMilliseconds(150)));
    }

    [Fact]
    public void Round_trip_preserves_field_values()
    {
        var dto = SensationFileSerializer.Deserialize(SpecExample);
        var json = SensationFileSerializer.Serialize(dto);
        var parsed = SensationFileSerializer.Deserialize(json);

        parsed.Should().BeEquivalentTo(dto);
    }

    [Fact]
    public void ParameterValue_with_two_oneof_variants_is_rejected()
    {
        const string ambiguous = """
            {
              "name": "x", "backend_kind": "y", "display_name": "z",
              "estimated_duration": "0s",
              "definition": {
                "microsensations": [{
                  "parameters": {
                    "weird": { "number": 1, "string_value": "two" }
                  }
                }]
              }
            }
            """;

        var act = () => SensationFileSerializer.Deserialize(ambiguous);

        act.Should().Throw<JsonException>()
            .WithMessage("*multiple oneof variants*");
    }

    [Fact]
    public void ParameterValue_with_no_oneof_variants_is_rejected()
    {
        const string empty = """
            {
              "name": "x", "backend_kind": "y", "display_name": "z",
              "estimated_duration": "0s",
              "definition": {
                "microsensations": [{
                  "parameters": {
                    "weird": { }
                  }
                }]
              }
            }
            """;

        var act = () => SensationFileSerializer.Deserialize(empty);

        act.Should().Throw<JsonException>()
            .WithMessage("*exactly one of*");
    }

    [Theory]
    [InlineData("0.4s", 400)]
    [InlineData("0s", 0)]
    [InlineData("1s", 1000)]
    [InlineData("40ms", 40)]
    [InlineData("100ms", 100)]
    public void Duration_parses_seconds_and_milliseconds(string input, int expectedMillis)
    {
        DurationStringConverter.Parse(input)
            .Should().Be(TimeSpan.FromMilliseconds(expectedMillis));
    }

    [Fact]
    public void Duration_rejects_unrecognised_units()
    {
        var act = () => DurationStringConverter.Parse("5min");

        act.Should().Throw<JsonException>()
            .WithMessage("*Unrecognised duration format*");
    }
}

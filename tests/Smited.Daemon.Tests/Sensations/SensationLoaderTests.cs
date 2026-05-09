using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Smited.Daemon.Backends;
using Smited.Daemon.Configuration;
using Smited.Daemon.Sensations;
using Smited.Daemon.Tests.Fixtures;
using Smited.V1;
using Xunit;

namespace Smited.Daemon.Tests.Sensations;

public class SensationLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "smited-loader-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static FakeBackend BuildOwoBackend()
    {
        var schema = new ParameterSchema();
        schema.Parameters.Add(new ParameterDef
        {
            Name = "frequency",
            Type = ParameterType.Number,
            Required = true,
            Min = 1,
            Max = 100,
            Description = "",
        });
        schema.Parameters.Add(new ParameterDef
        {
            Name = "intensity",
            Type = ParameterType.Number,
            Required = true,
            Min = 0,
            Max = 100,
            Description = "",
        });
        schema.Parameters.Add(new ParameterDef
        {
            Name = "duration",
            Type = ParameterType.Duration,
            Required = true,
            Min = 0,
            Max = 10,
            Description = "",
        });

        var topology = new ZoneTopology();
        topology.Zones.Add(new Zone { Id = "pectoral_l", DisplayName = "L pectoral" });
        topology.Zones.Add(new Zone { Id = "pectoral_r", DisplayName = "R pectoral" });
        var torso = new ZoneGroup { Id = "torso", DisplayName = "Torso" };
        torso.ZoneIds.Add("pectoral_l");
        torso.ZoneIds.Add("pectoral_r");
        topology.Groups.Add(torso);

        return new FakeBackend("mock-owo", kind: "owo_skin", capabilities: ["ems"])
        {
            Parameters = schema,
            Zones = topology,
        };
    }

    private SensationLoader BuildLoader(out SensationLibrary library, params FakeBackend[] backends)
    {
        var sink = new RecordingEventSink();
        var time = new FakeTimeProvider();
        var registry = new BackendRegistry(sink, time);
        foreach (var b in backends)
        {
            registry.Register(b);
        }
        var options = Options.Create(new SmitedOptions
        {
            Sensations = new SmitedOptions.SensationsOptions { LibraryRoot = _root },
        });
        library = new SensationLibrary(sink, time, options);
        return new SensationLoader(registry, library, time, options, NullLogger<SensationLoader>.Instance);
    }

    private void WriteSensation(string kindDir, string fileName, string json)
    {
        var dir = Path.Combine(_root, kindDir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), json);
    }

    [Fact]
    public async Task Loads_a_valid_sensation()
    {
        WriteSensation("owo_skin", "ping.json", """
            {
              "name": "ping",
              "backend_kind": "owo_skin",
              "display_name": "Ping",
              "description": "",
              "tags": ["test"],
              "default_zone_ids": ["pectoral_l"],
              "default_intensity": 50,
              "estimated_duration": "0.2s",
              "definition": {
                "microsensations": [
                  {
                    "parameters": {
                      "frequency": { "number": 50 },
                      "intensity": { "number": 50 },
                      "duration": { "duration": "0.2s" }
                    }
                  }
                ]
              }
            }
            """);

        var loader = BuildLoader(out var library, BuildOwoBackend());

        await loader.StartAsync(CancellationToken.None);

        library.Count.Should().Be(1);
        var s = library.Get("mock-owo", "ping")!;
        s.Tags.Should().BeEquivalentTo("test");
        s.DefaultIntensity.Should().Be(50);
    }

    [Fact]
    public async Task Aborts_on_malformed_json()
    {
        WriteSensation("owo_skin", "broken.json", "{ not valid json");

        var loader = BuildLoader(out _, BuildOwoBackend());

        var act = () => loader.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<SmitedStartupException>()
            .WithMessage("*broken.json*");
    }

    [Fact]
    public async Task Aborts_on_unknown_parameter_with_path_in_message()
    {
        WriteSensation("owo_skin", "unknown_param.json", """
            {
              "name": "x", "backend_kind": "owo_skin", "display_name": "x",
              "description": "", "tags": [], "default_zone_ids": [],
              "estimated_duration": "0.2s",
              "definition": {
                "microsensations": [
                  { "parameters": {
                      "frequency": { "number": 50 },
                      "intensity": { "number": 50 },
                      "duration": { "duration": "0.2s" },
                      "xyz": { "number": 1 }
                  } }
                ]
              }
            }
            """);

        var loader = BuildLoader(out _, BuildOwoBackend());

        var act = () => loader.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<SmitedStartupException>()
            .WithMessage("*microsensations[0].parameters.xyz*");
    }

    [Fact]
    public async Task Aborts_on_missing_required_parameter()
    {
        WriteSensation("owo_skin", "missing.json", """
            {
              "name": "x", "backend_kind": "owo_skin", "display_name": "x",
              "description": "", "tags": [], "default_zone_ids": [],
              "estimated_duration": "0.2s",
              "definition": {
                "microsensations": [
                  { "parameters": { "frequency": { "number": 50 } } }
                ]
              }
            }
            """);

        var loader = BuildLoader(out _, BuildOwoBackend());

        var act = () => loader.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<SmitedStartupException>()
            .WithMessage("*missing required*");
    }

    [Fact]
    public async Task Aborts_on_out_of_range_parameter()
    {
        WriteSensation("owo_skin", "oor.json", """
            {
              "name": "x", "backend_kind": "owo_skin", "display_name": "x",
              "description": "", "tags": [], "default_zone_ids": [],
              "estimated_duration": "0.2s",
              "definition": {
                "microsensations": [
                  { "parameters": {
                      "frequency": { "number": 50 },
                      "intensity": { "number": 999 },
                      "duration": { "duration": "0.2s" }
                  } }
                ]
              }
            }
            """);

        var loader = BuildLoader(out _, BuildOwoBackend());

        var act = () => loader.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<SmitedStartupException>()
            .WithMessage("*intensity*out of range*");
    }

    [Fact]
    public async Task Aborts_on_unknown_default_zone()
    {
        WriteSensation("owo_skin", "bad_zone.json", """
            {
              "name": "x", "backend_kind": "owo_skin", "display_name": "x",
              "description": "", "tags": [], "default_zone_ids": ["nonexistent"],
              "estimated_duration": "0.2s",
              "definition": {
                "microsensations": [
                  { "parameters": {
                      "frequency": { "number": 50 },
                      "intensity": { "number": 50 },
                      "duration": { "duration": "0.2s" }
                  } }
                ]
              }
            }
            """);

        var loader = BuildLoader(out _, BuildOwoBackend());

        var act = () => loader.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<SmitedStartupException>()
            .WithMessage("*nonexistent*");
    }

    [Fact]
    public async Task Skips_when_library_root_does_not_exist()
    {
        var loader = BuildLoader(out var library, BuildOwoBackend());
        // No directory created.

        await loader.StartAsync(CancellationToken.None);

        library.Count.Should().Be(0);
    }

    [Fact]
    public async Task Aborts_on_empty_microsensations_array()
    {
        WriteSensation("owo_skin", "empty.json", """
            {
              "name": "x", "backend_kind": "owo_skin", "display_name": "x",
              "description": "", "tags": [], "default_zone_ids": [],
              "estimated_duration": "0.2s",
              "definition": { "microsensations": [] }
            }
            """);

        var loader = BuildLoader(out _, BuildOwoBackend());

        var act = () => loader.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<SmitedStartupException>()
            .WithMessage("*empty definition.microsensations*");
    }

    [Fact]
    public async Task Aborts_on_default_intensity_out_of_range()
    {
        WriteSensation("owo_skin", "loud.json", """
            {
              "name": "x", "backend_kind": "owo_skin", "display_name": "x",
              "description": "", "tags": [], "default_zone_ids": [],
              "default_intensity": 999,
              "estimated_duration": "0.2s",
              "definition": {
                "microsensations": [
                  { "parameters": {
                      "frequency": { "number": 50 },
                      "intensity": { "number": 50 },
                      "duration": { "duration": "0.2s" }
                  } }
                ]
              }
            }
            """);

        var loader = BuildLoader(out _, BuildOwoBackend());

        var act = () => loader.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<SmitedStartupException>()
            .WithMessage("*default_intensity=999*");
    }

    [Fact]
    public async Task Required_parameter_check_is_case_insensitive()
    {
        WriteSensation("owo_skin", "case.json", """
            {
              "name": "x", "backend_kind": "owo_skin", "display_name": "x",
              "description": "", "tags": [], "default_zone_ids": [],
              "estimated_duration": "0.2s",
              "definition": {
                "microsensations": [
                  { "parameters": {
                      "Frequency": { "number": 50 },
                      "INTENSITY": { "number": 50 },
                      "duration":  { "duration": "0.2s" }
                  } }
                ]
              }
            }
            """);

        var loader = BuildLoader(out var library, BuildOwoBackend());

        await loader.StartAsync(CancellationToken.None);

        library.Count.Should().Be(1);
    }
}

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Smited.Daemon.Backends;
using Smited.Daemon.Tests.Fixtures;
using Smited.V1;
using Xunit;
using ZoneTopology = Smited.V1.ZoneTopology;

namespace Smited.Daemon.Tests.EndToEnd;

/// <summary>
/// E2E coverage for trigger-time overlap rejection. Two fake backends
/// declare placements on the same body region; the gRPC trigger path
/// observes the policy that's set in configuration.
/// </summary>
public class BodyMapOverlapTests
{
    [Fact]
    public async Task Off_policy_passes_overlapping_triggers_through()
    {
        using var fixture = NewFixture(policy:"Off");
        await SeedSensation(fixture);

        var response = await fixture.Client.TriggerAsync(new TriggerRequest
        {
            BackendId = "mock-owo",
            SensationName = "ping",
            ClientTraceId = "off-trace",
        });

        response.Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task Warn_policy_passes_overlapping_triggers_through()
    {
        using var fixture = NewFixture(policy:"Warn");
        await SeedSensation(fixture);

        var response = await fixture.Client.TriggerAsync(new TriggerRequest
        {
            BackendId = "mock-owo",
            SensationName = "ping",
            ClientTraceId = "warn-trace",
        });

        response.Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task Refuse_policy_rejects_overlapping_triggers_with_invalid_zone()
    {
        using var fixture = NewFixture(policy:"Refuse");
        await SeedSensation(fixture);

        var response = await fixture.Client.TriggerAsync(new TriggerRequest
        {
            BackendId = "mock-owo",
            SensationName = "ping",
            ClientTraceId = "refuse-trace",
        });

        response.Accepted.Should().BeFalse();
        response.Error.Code.Should().Be(TriggerErrorCode.InvalidZone);
        response.Error.Message.Should().Contain("'overlap-buddy'");
    }

    private static DaemonFixture NewFixture(string policy)
    {
        // mock-owo's pectoral_l zone overlaps with a fake "overlap-buddy"
        // backend's "z" zone in BodyRegion.ChestFront. The fake is
        // injected via the IEnumerable<IHapticBackend> DI seam; bodymap
        // placements are supplied via additionalConfig.
        var bodyMapConfig = new Dictionary<string, string?>
        {
            ["Smited:BodyMap:OverlapPolicy"] = policy,
            ["Smited:BodyMap:Placements:0:BackendId"] = "mock-owo",
            ["Smited:BodyMap:Placements:0:ZoneIds:0"] = "pectoral_l",
            ["Smited:BodyMap:Placements:0:Region"] = "ChestFront",
            ["Smited:BodyMap:Placements:1:BackendId"] = "overlap-buddy",
            ["Smited:BodyMap:Placements:1:ZoneIds:0"] = "z",
            ["Smited:BodyMap:Placements:1:Region"] = "ChestFront",
        };

        return new DaemonFixture(
            seed: root =>
            {
                SampleSensations.WriteOwo(root, "ping.json", PingSensation);
            },
            configureServices: services =>
            {
                services.AddSingleton<IHapticBackend>(_ => new FakeBackend("overlap-buddy")
                {
                    Zones = SingleZoneTopology("z"),
                });
            },
            additionalConfig: bodyMapConfig);
    }

    private const string PingSensation = """
        {
          "name": "ping",
          "backend_kind": "owo_skin",
          "display_name": "Ping",
          "description": "Tiny tick on left pectoral.",
          "default_zone_ids": ["pectoral_l"],
          "default_intensity": 50,
          "estimated_duration": "0.05s",
          "definition": {
            "microsensations": [
              {
                "parameters": {
                  "frequency": { "number": 50 },
                  "intensity": { "number": 50 },
                  "duration": { "duration": "0.05s" }
                }
              }
            ]
          }
        }
        """;

    private static Task SeedSensation(DaemonFixture fixture) => Task.CompletedTask;

    private static ZoneTopology SingleZoneTopology(string zoneId)
    {
        var topology = new ZoneTopology();
        topology.Zones.Add(new Zone { Id = zoneId, DisplayName = zoneId });
        return topology;
    }
}

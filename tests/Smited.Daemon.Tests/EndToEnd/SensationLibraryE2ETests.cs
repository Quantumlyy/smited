using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Smited.Daemon.Backends;
using Smited.Daemon.Tests.Fixtures;
using Smited.V1;
using Xunit;

namespace Smited.Daemon.Tests.EndToEnd;

public class SensationLibraryE2ETests : IDisposable
{
    private readonly DaemonFixture _fixture;

    public SensationLibraryE2ETests()
    {
        _fixture = new DaemonFixture(seed: root =>
        {
            SampleSensations.WriteOwo(root, "compile_error_mild.json", SampleSensations.CompileErrorMild);
            SampleSensations.WriteOwo(root, "deploy_success.json", SampleSensations.DeploySuccess);
        });
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task ListSensations_returns_files_loaded_at_boot()
    {
        var response = await _fixture.Client.ListSensationsAsync(new ListSensationsRequest());

        response.Sensations.Select(s => s.Name).Should().BeEquivalentTo(
            "compile_error_mild", "deploy_success");
    }

    [Fact]
    public async Task ListSensations_filters_by_tag()
    {
        var request = new ListSensationsRequest();
        request.Tags.Add("success");

        var response = await _fixture.Client.ListSensationsAsync(request);

        response.Sensations.Select(s => s.Name).Should().BeEquivalentTo("deploy_success");
    }

    [Fact]
    public async Task RegisterSensation_succeeds_for_a_capability_carrying_backend()
    {
        var sensation = BuildRegisteredSensation("brand_new", backendId: "mock-owo");

        var response = await _fixture.Client.RegisterSensationAsync(new RegisterSensationRequest
        {
            Sensation = sensation,
            Overwrite = false,
        });

        response.Registered.Should().BeTrue();
        response.Error.Should().BeEmpty();

        // ListSensations sees the new entry.
        var list = await _fixture.Client.ListSensationsAsync(new ListSensationsRequest());
        list.Sensations.Select(s => s.Name).Should().Contain("brand_new");
    }

    [Fact]
    public async Task UnregisterSensation_round_trips()
    {
        var sensation = BuildRegisteredSensation("ephemeral", backendId: "mock-owo");
        await _fixture.Client.RegisterSensationAsync(new RegisterSensationRequest
        {
            Sensation = sensation,
            Overwrite = false,
        });

        var response = await _fixture.Client.UnregisterSensationAsync(new UnregisterSensationRequest
        {
            BackendId = "mock-owo",
            Name = "ephemeral",
        });

        response.Unregistered.Should().BeTrue();
    }

    [Fact]
    public async Task RegisterSensation_for_unknown_backend_returns_registered_false_with_error()
    {
        var sensation = BuildRegisteredSensation("xyz", backendId: "no-such-backend");

        var response = await _fixture.Client.RegisterSensationAsync(new RegisterSensationRequest
        {
            Sensation = sensation,
        });

        response.Registered.Should().BeFalse();
        response.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RegisterSensation_with_unknown_default_zone_is_rejected()
    {
        var sensation = BuildRegisteredSensation("bad_zone", backendId: "mock-owo");
        sensation.DefaultZoneIds.Clear();
        sensation.DefaultZoneIds.Add("nonexistent_zone");

        var response = await _fixture.Client.RegisterSensationAsync(new RegisterSensationRequest
        {
            Sensation = sensation,
        });

        response.Registered.Should().BeFalse();
        response.Error.Should().Contain("default_zone_ids");
        response.Error.Should().Contain("nonexistent_zone");
    }

    [Fact]
    public async Task RegisterSensation_missing_required_parameter_is_rejected()
    {
        // Build a sensation that omits the backend's required `intensity`
        // parameter — protovalidate accepts the message shape but the
        // backend's schema demands intensity, so the daemon should refuse
        // to persist it (otherwise the next boot's loader aborts startup
        // on the same file).
        var inline = new InlineSensation();
        var micro = new Microsensation();
        micro.Parameters["frequency"] = new ParameterValue { Number = 50 };
        micro.Parameters["duration"] = new ParameterValue { Duration = Duration.FromTimeSpan(TimeSpan.FromMilliseconds(250)) };
        inline.Microsensations.Add(micro);

        var sensation = new RegisteredSensation
        {
            Name = "missing_required",
            BackendId = "mock-owo",
            DisplayName = "Missing Required",
            Description = "",
            EstimatedDuration = Duration.FromTimeSpan(TimeSpan.FromMilliseconds(250)),
            RegisteredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Definition = inline,
        };
        sensation.DefaultZoneIds.Add("pectoral_l");

        var response = await _fixture.Client.RegisterSensationAsync(new RegisterSensationRequest
        {
            Sensation = sensation,
        });

        response.Registered.Should().BeFalse();
        response.Error.Should().Contain("intensity");
    }

    [Fact]
    public async Task Runtime_registered_sensation_reloads_only_for_original_backend_id()
    {
        var root = Path.Combine(Path.GetTempPath(), "smited-runtime-scope-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var first = new DaemonFixture(
                       configureServices: services =>
                           services.AddSingleton<IHapticBackend>(BuildOwoLikeBackend("mock-owo-b")),
                       libraryRoot: root))
            {
                var response = await first.Client.RegisterSensationAsync(new RegisterSensationRequest
                {
                    Sensation = BuildRegisteredSensation("runtime_scoped", backendId: "mock-owo"),
                });

                response.Registered.Should().BeTrue();
            }

            using var second = new DaemonFixture(
                configureServices: services =>
                    services.AddSingleton<IHapticBackend>(BuildOwoLikeBackend("mock-owo-b")),
                libraryRoot: root);

            var backendA = await second.Client.ListSensationsAsync(new ListSensationsRequest
            {
                BackendId = "mock-owo",
            });
            var backendB = await second.Client.ListSensationsAsync(new ListSensationsRequest
            {
                BackendId = "mock-owo-b",
            });

            backendA.Sensations.Select(s => s.Name).Should().Contain("runtime_scoped");
            backendB.Sensations.Select(s => s.Name).Should().NotContain("runtime_scoped");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static RegisteredSensation BuildRegisteredSensation(string name, string backendId)
    {
        var inline = new InlineSensation();
        var micro = new Microsensation();
        micro.Parameters["frequency"] = new ParameterValue { Number = 40 };
        micro.Parameters["intensity"] = new ParameterValue { Number = 50 };
        micro.Parameters["duration"] = new ParameterValue { Duration = Duration.FromTimeSpan(TimeSpan.FromMilliseconds(250)) };
        inline.Microsensations.Add(micro);

        var registered = new RegisteredSensation
        {
            Name = name,
            BackendId = backendId,
            DisplayName = name.Replace('_', ' '),
            Description = "",
            EstimatedDuration = Duration.FromTimeSpan(TimeSpan.FromMilliseconds(250)),
            RegisteredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Definition = inline,
        };
        registered.DefaultZoneIds.Add("pectoral_l");
        return registered;
    }

    private static FakeBackend BuildOwoLikeBackend(string id) =>
        new(id, kind: "owo_skin", displayName: id, capabilities: ["ems", "zoned", "sensation_registry_mutable"]);
}

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Pishock;
using Smited.Daemon.Pishock.Internal;
using Smited.Daemon.Sensations;
using Xunit;

namespace Smited.Daemon.Tests.Backends.Pishock;

/// <summary>
/// Authoring-time validation of the bundled <c>sensations/pishock/*.json</c>
/// files: every file deserializes, declares the right kind, declares
/// only Vibrate/Beep ops (no Shock in bundled defaults), and validates
/// against the mock backend's parameter schema. Catches authoring bugs
/// before they reach a daemon at startup, where the only feedback would
/// be a refused-startup error.
/// </summary>
public class BundledPishockSensationsTests
{
    private static readonly string SensationsRoot = LocateSensationsRoot();

    public static IEnumerable<object[]> AllFiles =>
        Directory.EnumerateFiles(SensationsRoot, "*.json")
            .Select(path => new object[] { Path.GetFileName(path) });

    [Theory]
    [MemberData(nameof(AllFiles))]
    public void File_deserializes_with_backend_kind_pishock(string filename)
    {
        var dto = LoadFile(filename);

        dto.BackendKind.Should().Be("pishock");
        dto.Name.Should().NotBeNullOrEmpty();
        dto.DisplayName.Should().NotBeNullOrEmpty();
        dto.DefaultZoneIds.Should().BeEquivalentTo(new[] { "shock" });
        dto.Definition.Microsensations.Should().NotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(AllFiles))]
    public void File_validates_against_default_pishock_parameter_schema(string filename)
    {
        var dto = LoadFile(filename);

        // Validate using the default-options schema — i.e. AllowedOps =
        // [Vibrate, Beep], no Shock. Any bundled sensation that needs
        // Shock would fail here, which is intentional: the bundled
        // library is "useful for dev workflow feedback," not
        // "ready-to-fire pain." Users opt into Shock by enabling it in
        // AllowedOps AND authoring their own sensations.
        var backend = new MockPishockBackend(
            "test-validation",
            new PishockBackendOptions(),
            new FakeTimeProvider(),
            NullLogger<MockPishockBackend>.Instance);

        var micros = dto.Definition.Microsensations
            .Select(m => new MicrosensationParameters(m.Parameters))
            .ToArray();

        var failure = SensationValidator.Validate(micros, dto.DefaultZoneIds, backend);
        failure.Should().BeNull(
            "every bundled sensation must validate against a default mock_pishock backend "
            + "(AllowedOps=[Vibrate, Beep]); a sensation that fails here would refuse daemon "
            + "startup with this same error");
    }

    [Theory]
    [MemberData(nameof(AllFiles))]
    public void File_estimated_duration_matches_summed_microsensation_durations(string filename)
    {
        var dto = LoadFile(filename);

        var micros = dto.Definition.Microsensations
            .Select(m => new MicrosensationParameters(m.Parameters))
            .ToArray();
        var request = new BackendTriggerRequest(
            SensationId: "_unused",
            SensationName: dto.Name,
            ZoneIds: dto.DefaultZoneIds,
            IntensityScale: null,
            Priority: 0,
            ClientTraceId: "_unused",
            Microsensations: micros);

        // Authored estimated_duration is transport-agnostic — it reflects
        // the millisecond intent in the file. Use LAN mode so the helper
        // returns ms-precise sums without applying cloud's whole-second
        // rounding.
        var computed = MicrosensationReader.ComputeEstimatedDuration(
            request, PishockTransportMode.Lan);

        // The authored estimated_duration drives admin-UI countdown
        // displays and the daemon's history-row "expected" column.
        // Drift between authored and computed makes both wrong.
        computed.Should().Be(dto.EstimatedDuration);
    }

    [Fact]
    public void Bundled_set_includes_every_planned_sensation()
    {
        var names = Directory.EnumerateFiles(SensationsRoot, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .ToHashSet();

        names.Should().BeEquivalentTo(new[]
        {
            "compile_error_mild",
            "compile_error_severe",
            "deploy_success",
            "deploy_failure",
            "pr_merged",
            "notification",
        });
    }

    private static SensationFileDto LoadFile(string filename)
    {
        var path = Path.Combine(SensationsRoot, filename);
        return SensationFileSerializer.Deserialize(File.ReadAllText(path));
    }

    /// <summary>
    /// Walks up from the test assembly's directory to find the workspace
    /// root (marked by <c>smited.sln</c>) so the test reads the
    /// authored files in <c>sensations/pishock/</c> rather than a copy
    /// in the test bin. The bundled files are part of the source tree
    /// and the test verifies the source state.
    /// </summary>
    private static string LocateSensationsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "smited.sln")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new InvalidOperationException(
                "Could not find workspace root (smited.sln) walking up from "
                + AppContext.BaseDirectory);
        }
        return Path.Combine(dir.FullName, "sensations", "pishock");
    }
}

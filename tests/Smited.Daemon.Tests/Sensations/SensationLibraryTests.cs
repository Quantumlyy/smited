using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Configuration;
using Smited.Daemon.Sensations;
using Smited.Daemon.Tests.Fixtures;
using Xunit;

namespace Smited.Daemon.Tests.Sensations;

public class SensationLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "smited-lib-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static SensationLibrary CreateLibrary(out RecordingEventSink sink)
    {
        sink = new RecordingEventSink();
        return new SensationLibrary(sink, new FakeTimeProvider(), Options.Create(new SmitedOptions()));
    }

    private SensationLibrary CreatePersistedLibrary(out RecordingEventSink sink)
    {
        sink = new RecordingEventSink();
        var options = Options.Create(new SmitedOptions
        {
            Sensations = new SmitedOptions.SensationsOptions { LibraryRoot = _root },
        });
        return new SensationLibrary(sink, new FakeTimeProvider(), options);
    }

    private static RegisteredSensation MakeSensation(
        string name = "compile_error_mild",
        string backendId = "mock-owo",
        IReadOnlyList<string>? tags = null) =>
        new(
            Name: name,
            BackendId: backendId,
            DisplayName: name,
            Description: "",
            Tags: tags ?? Array.Empty<string>(),
            DefaultZoneIds: Array.Empty<string>(),
            DefaultIntensity: null,
            EstimatedDuration: TimeSpan.FromMilliseconds(200),
            RegisteredAt: DateTimeOffset.UtcNow,
            Definition: Array.Empty<MicrosensationParameters>());

    [Fact]
    public void Register_then_Get_returns_the_same_record()
    {
        var lib = CreateLibrary(out _);
        var s = MakeSensation();

        lib.Register(s, overwrite: false).Should().BeTrue();

        lib.Get("mock-owo", "compile_error_mild").Should().Be(s);
    }

    [Fact]
    public void Register_duplicate_without_overwrite_returns_false()
    {
        var lib = CreateLibrary(out _);
        lib.Register(MakeSensation(), overwrite: false);

        lib.Register(MakeSensation(), overwrite: false).Should().BeFalse();
    }

    [Fact]
    public void Register_duplicate_with_overwrite_replaces()
    {
        var lib = CreateLibrary(out _);
        lib.Register(MakeSensation(), overwrite: false);

        var replacement = MakeSensation() with { DisplayName = "Replaced" };
        lib.Register(replacement, overwrite: true).Should().BeTrue();

        lib.Get("mock-owo", "compile_error_mild")!.DisplayName.Should().Be("Replaced");
    }

    [Fact]
    public void Unregister_removes_and_signals_an_event()
    {
        var lib = CreateLibrary(out var sink);
        lib.Register(MakeSensation(), overwrite: false);

        lib.Unregister("mock-owo", "compile_error_mild").Should().BeTrue();

        lib.Get("mock-owo", "compile_error_mild").Should().BeNull();
        sink.Events.Should().HaveCount(2); // Registered + Unregistered
        sink.Events[1].Should().BeOfType<SensationRegistryChangedEvent>()
            .Which.Change.Should().Be(SensationRegistryChange.Unregistered);
    }

    [Fact]
    public void Unregister_unknown_returns_false()
    {
        var lib = CreateLibrary(out _);

        lib.Unregister("mock-owo", "nope").Should().BeFalse();
    }

    [Fact]
    public void List_filters_by_backend_id()
    {
        var lib = CreateLibrary(out _);
        lib.Register(MakeSensation("a", backendId: "mock-owo"), overwrite: false);
        lib.Register(MakeSensation("b", backendId: "other"), overwrite: false);

        lib.List(backendId: "mock-owo", tags: null)
            .Select(s => s.Name).Should().BeEquivalentTo("a");
    }

    [Fact]
    public void List_filters_by_tags_requiring_every_tag()
    {
        var lib = CreateLibrary(out _);
        lib.Register(MakeSensation("a", tags: ["build", "error", "severe"]), overwrite: false);
        lib.Register(MakeSensation("b", tags: ["build", "error"]), overwrite: false);
        lib.Register(MakeSensation("c", tags: ["chat"]), overwrite: false);

        lib.List(backendId: null, tags: ["build", "severe"])
            .Select(s => s.Name).Should().BeEquivalentTo("a");
    }

    [Fact]
    public void Register_publishes_event_with_backend_and_name()
    {
        var lib = CreateLibrary(out var sink);

        lib.Register(MakeSensation("ping", backendId: "mock-owo"), overwrite: false);

        var evt = sink.Events.Should().ContainSingle()
            .Subject.Should().BeOfType<SensationRegistryChangedEvent>().Subject;
        evt.BackendId.Should().Be("mock-owo");
        evt.SensationName.Should().Be("ping");
        evt.Change.Should().Be(SensationRegistryChange.Registered);
    }

    [Fact]
    public async Task RegisterAsync_writes_a_file_at_the_expected_path()
    {
        var lib = CreatePersistedLibrary(out _);
        var sensation = MakeSensation("ping_disk");

        var ok = await lib.RegisterAsync(sensation, "owo_skin", overwrite: false, CancellationToken.None);

        ok.Should().BeTrue();
        var path = Path.Combine(_root, "owo_skin", "ping_disk.json");
        File.Exists(path).Should().BeTrue();

        // Re-parse with the existing reader to confirm symmetry.
        var roundTrip = SensationFileSerializer.Deserialize(File.ReadAllText(path));
        roundTrip.Name.Should().Be("ping_disk");
        roundTrip.BackendKind.Should().Be("owo_skin");
        roundTrip.Scope.Should().Be(SensationFileScope.Id);
        roundTrip.BackendId.Should().Be("mock-owo");
    }

    [Fact]
    public async Task RegisterAsync_returns_false_without_overwrite_when_entry_exists()
    {
        var lib = CreatePersistedLibrary(out _);
        var sensation = MakeSensation("ping");
        await lib.RegisterAsync(sensation, "owo_skin", overwrite: false, CancellationToken.None);

        var second = await lib.RegisterAsync(sensation, "owo_skin", overwrite: false, CancellationToken.None);

        second.Should().BeFalse();
    }

    [Fact]
    public async Task RegisterAsync_overwrites_when_overwrite_is_true()
    {
        var lib = CreatePersistedLibrary(out _);
        await lib.RegisterAsync(MakeSensation("ping"), "owo_skin", overwrite: false, CancellationToken.None);

        var replacement = MakeSensation("ping") with { DisplayName = "Replaced" };
        var ok = await lib.RegisterAsync(replacement, "owo_skin", overwrite: true, CancellationToken.None);

        ok.Should().BeTrue();
        lib.Get("mock-owo", "ping")!.DisplayName.Should().Be("Replaced");
    }

    [Fact]
    public async Task UnregisterAsync_deletes_the_on_disk_file()
    {
        var lib = CreatePersistedLibrary(out _);
        await lib.RegisterAsync(MakeSensation("ephemeral"), "owo_skin", overwrite: false, CancellationToken.None);
        var path = Path.Combine(_root, "owo_skin", "ephemeral.json");
        File.Exists(path).Should().BeTrue();

        var removed = await lib.UnregisterAsync("mock-owo", "owo_skin", "ephemeral", CancellationToken.None);

        removed.Should().BeTrue();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task UnregisterAsync_tolerates_an_already_missing_file()
    {
        var lib = CreatePersistedLibrary(out _);
        await lib.RegisterAsync(MakeSensation("ghost"), "owo_skin", overwrite: false, CancellationToken.None);
        var path = Path.Combine(_root, "owo_skin", "ghost.json");
        File.Delete(path);

        var removed = await lib.UnregisterAsync("mock-owo", "owo_skin", "ghost", CancellationToken.None);

        removed.Should().BeTrue();
    }
}

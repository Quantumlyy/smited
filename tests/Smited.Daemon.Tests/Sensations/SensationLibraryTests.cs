using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Sensations;
using Smited.Daemon.Tests.Fixtures;
using Xunit;

namespace Smited.Daemon.Tests.Sensations;

public class SensationLibraryTests
{
    private static SensationLibrary CreateLibrary(out RecordingEventSink sink)
    {
        sink = new RecordingEventSink();
        return new SensationLibrary(sink, new FakeTimeProvider());
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
}

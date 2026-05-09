using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Smited.Daemon.Backends;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Tests.Fixtures;
using Xunit;

namespace Smited.Daemon.Tests.Backends;

public class BackendRegistryTests
{
    private static BackendRegistry CreateRegistry(
        out RecordingEventSink sink,
        out FakeTimeProvider time)
    {
        sink = new RecordingEventSink();
        time = new FakeTimeProvider();
        return new BackendRegistry(sink, time);
    }

    [Fact]
    public void Register_then_TryGet_returns_the_same_instance()
    {
        var registry = CreateRegistry(out _, out _);
        var fake = new FakeBackend("alpha");

        registry.Register(fake);

        registry.TryGet("alpha").Should().BeSameAs(fake);
    }

    [Fact]
    public void Register_duplicate_id_throws()
    {
        var registry = CreateRegistry(out _, out _);
        registry.Register(new FakeBackend("alpha"));

        var act = () => registry.Register(new FakeBackend("alpha"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'alpha'*already registered*");
    }

    [Fact]
    public void Deregister_unknown_id_returns_false()
    {
        var registry = CreateRegistry(out _, out _);

        registry.Deregister("nope").Should().BeFalse();
    }

    [Fact]
    public void Deregister_existing_returns_true_and_removes()
    {
        var registry = CreateRegistry(out _, out _);
        registry.Register(new FakeBackend("alpha"));

        registry.Deregister("alpha").Should().BeTrue();
        registry.TryGet("alpha").Should().BeNull();
    }

    [Fact]
    public void WhereCapability_returns_only_matching_backends()
    {
        var registry = CreateRegistry(out _, out _);
        registry.Register(new FakeBackend("a", capabilities: ["ems", "zoned"]));
        registry.Register(new FakeBackend("b", capabilities: ["zoned"]));
        registry.Register(new FakeBackend("c", capabilities: ["ems"]));

        registry.WhereCapability("ems").Select(b => b.Id)
            .Should().BeEquivalentTo("a", "c");
    }

    [Fact]
    public void Register_publishes_a_BackendLifecycleEvent_with_snapshot()
    {
        var registry = CreateRegistry(out var sink, out var time);
        time.SetUtcNow(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero));
        var fake = new FakeBackend("alpha", kind: "owo_skin", capabilities: ["ems"]);

        registry.Register(fake);

        var evt = sink.Events.Should().ContainSingle().Subject;
        evt.Should().BeOfType<BackendLifecycleEvent>();
        var lifecycle = (BackendLifecycleEvent)evt;
        lifecycle.BackendId.Should().Be("alpha");
        lifecycle.Change.Should().Be(BackendLifecycleChange.Registered);
        lifecycle.Snapshot.Kind.Should().Be("owo_skin");
        lifecycle.Snapshot.Capabilities.Should().BeEquivalentTo("ems");
        lifecycle.Timestamp.Should().Be(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Deregister_publishes_a_Deregistered_lifecycle_event()
    {
        var registry = CreateRegistry(out var sink, out _);
        registry.Register(new FakeBackend("alpha"));

        registry.Deregister("alpha").Should().BeTrue();

        sink.Events.Should().HaveCount(2);
        sink.Events[1].Should().BeOfType<BackendLifecycleEvent>()
            .Which.Change.Should().Be(BackendLifecycleChange.Deregistered);
    }

    [Fact]
    public void All_returns_every_registered_backend()
    {
        var registry = CreateRegistry(out _, out _);
        registry.Register(new FakeBackend("a"));
        registry.Register(new FakeBackend("b"));

        registry.All.Select(b => b.Id).Should().BeEquivalentTo("a", "b");
        registry.Count.Should().Be(2);
    }
}

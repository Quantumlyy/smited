using System.Collections.Immutable;
using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using Smited.Daemon.Backends;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.BodyMap;
using Smited.V1;

namespace Smited.Daemon.Tests.Fixtures;

/// <summary>
/// Minimal <see cref="IHapticBackend"/> implementation for unit tests that
/// only need static descriptor data. Triggering and stopping are stubbed —
/// suite-specific backends override.
/// </summary>
internal sealed class FakeBackend : IHapticBackend
{
    private readonly Channel<BackendEvent> _events = Channel.CreateUnbounded<BackendEvent>();

    public FakeBackend(
        string id = "fake",
        string kind = "fake_kind",
        string displayName = "Fake Backend",
        IReadOnlyList<string>? capabilities = null,
        BackendStatus status = BackendStatus.Ready)
    {
        Id = id;
        Kind = kind;
        DisplayName = displayName;
        Capabilities = capabilities ?? Array.Empty<string>();
        Status = status;
    }

    public string Id { get; }

    public string Kind { get; }

    public string DisplayName { get; }

    public BackendStatus Status { get; }

    public IReadOnlyList<string> Capabilities { get; }

    public ZoneTopology Zones { get; init; } = new();

    public ParameterSchema Parameters { get; init; } = new();

    public ConcurrencyModel Concurrency { get; init; } = new()
    {
        MaxConcurrent = 1,
        Policy = ConcurrencyPolicy.RejectNew,
    };

    public CalibrationState? Calibration { get; init; }

    public Struct? Extras { get; init; }

    public IReadOnlySet<BodyRegion> ForbiddenRegions { get; init; } =
        ImmutableHashSet<BodyRegion>.Empty;

    public Func<BackendTriggerRequest, CancellationToken, Task<BackendTriggerResult>>? OnTrigger { get; set; }

    public Func<BackendStopRequest, CancellationToken, Task<int>>? OnStop { get; set; }

    public Func<CancellationToken, Task>? OnConnect { get; set; }

    public Task ConnectAsync(CancellationToken ct) =>
        OnConnect?.Invoke(ct) ?? Task.CompletedTask;

    public Task<BackendTriggerResult> TriggerAsync(BackendTriggerRequest request, CancellationToken ct) =>
        OnTrigger?.Invoke(request, ct) ?? Task.FromResult(new BackendTriggerResult(request.SensationId, TimeSpan.Zero));

    public Task<int> StopAsync(BackendStopRequest request, CancellationToken ct) =>
        OnStop?.Invoke(request, ct) ?? Task.FromResult(0);

    public IAsyncEnumerable<BackendEvent> Events => _events.Reader.ReadAllAsync();

    public void Emit(BackendEvent evt) => _events.Writer.TryWrite(evt);

    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

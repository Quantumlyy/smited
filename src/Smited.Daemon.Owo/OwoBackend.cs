// TODO: real OWO SDK wiring — see docs/adding-a-backend.md
//
// This file is excluded from compile on non-Windows hosts via the
// conditional <Compile Remove="OwoBackend.cs"/> ItemGroup in
// Smited.Daemon.Owo.csproj. Every member throws NotImplementedException
// so a future Windows finishing pass has clean stubs to fill in.

#if WINDOWS
using Google.Protobuf.WellKnownTypes;
using Smited.Daemon.Backends;
using Smited.Daemon.Backends.Internal;
using Smited.V1;

namespace Smited.Daemon.Owo;

/// <summary>
/// Real OWO Skin haptic backend. Stub implementation — every member
/// throws <see cref="NotImplementedException"/> until a Windows
/// finishing pass wires the actual <c>MyOWO</c> SDK calls.
/// </summary>
public sealed class OwoBackend : IHapticBackend
{
    public string Id => throw new NotImplementedException();

    public string Kind => throw new NotImplementedException();

    public string DisplayName => throw new NotImplementedException();

    public BackendStatus Status => throw new NotImplementedException();

    public IReadOnlyList<string> Capabilities => throw new NotImplementedException();

    public ZoneTopology Zones => throw new NotImplementedException();

    public ParameterSchema Parameters => throw new NotImplementedException();

    public ConcurrencyModel Concurrency => throw new NotImplementedException();

    public CalibrationState? Calibration => throw new NotImplementedException();

    public Struct? Extras => throw new NotImplementedException();

    public IAsyncEnumerable<BackendEvent> Events => throw new NotImplementedException();

    public Task ConnectAsync(CancellationToken ct) =>
        throw new NotImplementedException();

    public Task<BackendTriggerResult> TriggerAsync(BackendTriggerRequest request, CancellationToken ct) =>
        throw new NotImplementedException();

    public Task<int> StopAsync(BackendStopRequest request, CancellationToken ct) =>
        throw new NotImplementedException();

    public ValueTask DisposeAsync() =>
        throw new NotImplementedException();
}
#endif

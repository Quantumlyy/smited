# Adding a new haptic backend

Every backend implements `IHapticBackend` (in `src/Smited.Daemon/Backends/`). The daemon doesn't care about the underlying transport — BLE, USB-HID, OSC, a virtual loopback — only that the backend exposes its capabilities through the interface and pushes lifecycle events through its `IAsyncEnumerable<BackendEvent>`.

## Walkthrough

### 1. Pick where the backend lives

- **Cross-platform** (no OS-specific SDKs) → put it under `src/Smited.Daemon/Backends/<YourKind>/`.
- **Platform-conditional** (e.g. a Windows-only or macOS-only SDK) → its own csproj alongside `Smited.Daemon.Owo`. Use the same conditional-compilation pattern (see below).

### 2. Implement `IHapticBackend`

```csharp
public sealed class HapticVestBackend : IHapticBackend
{
    public string Id          => "haptic-vest-1";   // unique runtime id
    public string Kind        => "haptic_vest";     // hardware family
    public string DisplayName => "Acme Haptic Vest";
    public BackendStatus Status => BackendStatus.Ready;

    public IReadOnlyList<string> Capabilities => new[]
    {
        "vibration", "zoned",
        // Add "sensation_registry_mutable" only if the backend can accept
        // runtime-registered sensations and persist them across restarts.
    };

    public ZoneTopology Zones { get; }            // build once in the ctor
    public ParameterSchema Parameters { get; }    //
    public ConcurrencyModel Concurrency { get; }  //
    public CalibrationState? Calibration { get; private set; }
    public Struct? Extras => null;

    public IAsyncEnumerable<BackendEvent> Events => _events.Reader.ReadAllAsync();

    public Task ConnectAsync(CancellationToken ct) { /* open the device */ }
    public Task<BackendTriggerResult> TriggerAsync(BackendTriggerRequest req, CancellationToken ct) { /* play */ }
    public Task<int> StopAsync(BackendStopRequest req, CancellationToken ct) { /* cancel */ }
    public ValueTask DisposeAsync() { /* close the device */ }
}
```

The trigger/stop/event flow uses internal records (`Backends/Internal/`), not generated proto types — that's intentional. Static descriptors (`Zones`, `Parameters`, `Concurrency`, `Calibration`, `Extras`) reuse proto types because they round-trip 1:1 to the wire.

### 3. Take a `TimeProvider` dependency

Every concurrency, delay, or scheduling concern goes through `TimeProvider` so tests can use `FakeTimeProvider` to fast-forward. `Task.Delay(span, timeProvider, ct)` is the standard incantation. See `MockOwoBackend.TriggerAsync` for the canonical pattern.

### 4. Emit events through your own `Channel<BackendEvent>`

```csharp
private readonly Channel<BackendEvent> _events = Channel.CreateUnbounded<BackendEvent>();

private void Emit(BackendEvent evt) => _events.Writer.TryWrite(evt);
```

Fire `SensationStarted` immediately on accept, `SensationCompleted` on natural finish, `SensationCancelled` on cancellation. The `BackendBootstrapper` spins up a fan-out task per backend that forwards every event into the daemon's `EventBus` — you don't write to the bus directly.

### 5. Wire it into DI

`Program.cs`:

```csharp
builder.Services.AddSingleton<HapticVestBackend>();
```

Then in `BackendBootstrapper.StartAsync`, register conditionally:

```csharp
if (_options.Backends.EnableHapticVest)
{
    var vest = _services.GetRequiredService<HapticVestBackend>();
    RegisterAndFan(vest);
}
```

Add the `EnableHapticVest` flag to `SmitedOptions.BackendsOptions` and to `appsettings.json`.

### 6. Sensation library directory

Drop a `sensations/<your_kind>/*.json` directory at the repo root (or wherever `Smited:Sensations:LibraryRoot` points). At boot, `SensationLoader` picks up files whose `backend_kind` matches the backend's `Kind` field. Files with the default `scope: "kind"` bind to every backend instance of that kind; files with `scope: "id"` bind only to their `backend_id` and are skipped if that backend is absent. Schema validation (parameter types, ranges, required fields, zone IDs) runs against each target backend's `ParameterSchema` and `ZoneTopology` before the daemon finishes starting; a failing file aborts startup with the path and offending field.

If the backend should accept runtime registrations via the `RegisterSensation` RPC, advertise the `sensation_registry_mutable` capability tag. Runtime registrations are written to `LibraryRoot/<your_kind>/<name>.json` with `scope: "id"` and `backend_id` set to the backend that accepted the request — they survive across daemon restarts without leaking onto sibling backends of the same kind. Authored files can omit `scope` to keep the broader kind-level behavior.

## Platform-conditional backends (Windows-only example)

`Smited.Daemon.Owo` is the reference implementation — see [`docs/owo.md`](owo.md) for the user-facing setup, runbook, and TENS safety notes the OWO backend ships with. The structural pattern:

1. **`src/Smited.Daemon.<Platform>/<Platform>Backend.csproj`** — `<TargetFramework>net9.0-windows</TargetFramework>` (or whatever TFM). The `<PropertyGroup>` is **unconditional** — conditioning it produces an empty `TargetFramework` on the wrong platform and breaks the build.
2. The platform's NuGet package goes in a conditional `<ItemGroup Condition="'$(OS)' == 'Windows_NT'">`.
3. `<Compile Remove>` every SDK-touching `.cs` file in a non-platform `<ItemGroup>` so the project compiles to an empty assembly off-platform. Cross-platform helpers (records, options, interfaces with primitive-only signatures) can stay shared.
4. The platform project references `Smited.Daemon.Abstractions` (so it can see `IHapticBackend` and any cross-platform helper types like `OwoBackendOptions`/`IOwoSdk`).
5. The daemon project's reverse `ProjectReference` is conditional and uses `<ReferenceOutputAssembly>false</ReferenceOutputAssembly>` so the build graph stays acyclic. Daemon source never imports the backend type — `BackendBootstrapper` loads it via `Type.GetType("Smited.Daemon.<Platform>.<Platform>Backend, Smited.Daemon.<Platform>")` at runtime. Auxiliary singletons the backend depends on (e.g. an `IOwoSdk` impl) follow the same reflective-registration pattern in `Program.cs`.

## Tests

Add unit tests under `tests/Smited.Daemon.Tests/Backends/<YourKind>BackendTests.cs`:

- Static descriptors match your spec.
- Trigger emits Started → Completed across the estimated duration (use `FakeTimeProvider`).
- Stop emits Cancelled and returns the correct count.
- Calibration / status changes emit the matching events.

For end-to-end coverage with a real gRPC client, register your backend in `DaemonFixture` and add a test class under `tests/Smited.Daemon.Tests/EndToEnd/`.

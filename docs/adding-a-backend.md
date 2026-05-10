# Adding a new haptic backend

Every backend implements `IHapticBackend` (in `src/Smited.Daemon/Backends/`). The daemon doesn't care about the underlying transport — BLE, USB-HID, OSC, a virtual loopback — only that the backend exposes its capabilities through the interface and pushes lifecycle events through its `IAsyncEnumerable<BackendEvent>`.

## Backend taxonomy

Two hardware families ship in-tree. They differ on every meaningful axis, which is the whole point of the abstraction: if a backend that's this dissimilar fits the schema cleanly, the schema is defensible.

- **OWO Skin** — `Kind = "owo_skin"`. EMS / TENS-based stimulation. 10 zones (pectoral / abdominal / lumbar / dorsal pairs plus left/right arm). Exclusive concurrency: `CONCURRENCY_POLICY_CANCEL_OLDEST`, `max_concurrent = 1`. Per-user calibration captured as a percentage of the user's pain threshold; advertises the `calibrated` capability tag. Real backend lives in `Smited.Daemon.Owo` (Windows-only); the Mac-runnable mock is `MockOwoBackend`.
- **bHaptics TactSuit** — `Kind = "bhaptics_tactsuit"`. Vibration-motor haptics. 40 vest zones (front/back halves on a 4×5 grid), with optional 12 glove + 8 forearm motors. Concurrent: `CONCURRENCY_POLICY_PRIORITY`, `max_concurrent = 4` (motors physically sum on hardware; the cap prevents runaway haptic stacking). No per-user calibration — intensity is tuned via the bHaptics Player app's global slider. Real backend lives in `Smited.Daemon.Bhaptics` (Windows-only, speaks the local Player WebSocket); the Mac-runnable mock is `MockBhapticsBackend`.

When weighing a new backend, ask whether it fits one of these `Kind` values cleanly. If the device shape is genuinely different (different transport, different calibration semantics, different concurrency model), it's its own family — pick a new `Kind` and a new directory under `sensations/<your_kind>/`.

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

    // Manufacturer-mandated forbidden regions; non-overridable.
    // Backends with no manufacturer-stated bans return Empty; see
    // step 6 below for when to populate this and docs/body-map.md
    // for the smited-default forbidden regions every backend
    // inherits on top.
    public IReadOnlySet<BodyRegion> ForbiddenRegions { get; } =
        ImmutableHashSet<BodyRegion>.Empty;

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

### 5. Implement an `IBackendFactory` and wire it into DI

`BackendBootstrapper` does not special-case any backend kind. It iterates `Smited:Backends:Items[]`, resolves a matching `IBackendFactory` (case-insensitive on `Kind`), and asks the factory to build the backend from the descriptor and its `Options` sub-section. To add a new backend you ship one factory:

```csharp
internal sealed class HapticVestBackendFactory : IBackendFactory
{
    public string Kind => "haptic_vest";

    public IHapticBackend? TryCreate(
        BackendDescriptor descriptor,
        IConfigurationSection optionsSection,
        IServiceProvider services,
        ILogger logger)
    {
        var options = optionsSection.Get<HapticVestBackendOptions>() ?? new HapticVestBackendOptions();
        if (!string.IsNullOrEmpty(descriptor.Id))
        {
            options.BackendId = descriptor.Id;
        }
        return ActivatorUtilities.CreateInstance<HapticVestBackend>(services, options);
    }
}
```

Then register the factory next to the other built-ins in `Program.cs` (or a service-collection extension):

```csharp
services.TryAddEnumerable(
    ServiceDescriptor.Singleton<IBackendFactory, HapticVestBackendFactory>());
```

Return `null` from `TryCreate` if the runtime environment can't host the backend (wrong OS, missing assembly, broken runtime dep). Throw only on **user-fixable misconfiguration**; the bootstrapper logs a null-return as `INFO` and continues with the next descriptor.

Users opt the backend in by adding a descriptor to their config:

```json
{
  "Smited": {
    "Backends": {
      "Items": [
        {
          "Kind": "haptic_vest",
          "Id": "vest-primary",
          "Enabled": true,
          "Options": {
            "GameDisplayName": "smited haptic daemon"
          }
        }
      ]
    }
  }
}
```

`BackendDescriptor.Id` is what clients use as `backend_id` over the wire and what the bodymap framework keys placements on.

#### Singleton kinds

A backend kind is a **singleton kind** when its factory's underlying state can't be safely partitioned across multiple instances. The descriptor validator rejects configurations that declare more than one **enabled** descriptor of a singleton kind; disabled descriptors of the same kind are fine, since they don't reach the factory. The canonical list lives in `BackendDescriptorValidator.SingletonKinds`. Today:

- **`mock_owo`** — registered as a DI singleton so `IMockOwoController` (used by tests and the future control surface) has a stable target. A second descriptor of the same kind would compete for the same instance's `OverrideId` / `OverrideDisplayName` overrides.
- **`owo_skin`** — depends on a process-wide static `IOwoSdk`. The OWO SDK binds to one suit per process; two enabled `owo_skin` descriptors would race on `Send` / `Stop` and the device would fire whatever the most recent caller asked for.

If you're adding a new backend kind whose factory shares state across instances (process-wide SDK, single hardware connection, file lock), add the kind to `SingletonKinds` so the validator catches misconfigurations up-front. Most new kinds are **not** singletons — bHaptics and PiShock both support multi-instance operation because each device has its own connection state, and the daemon should let users register one descriptor per device.

For non-singleton kinds: implement `TryCreate` so it issues a fresh `IHapticBackend` per call (`ActivatorUtilities.CreateInstance` does this naturally — its returned object isn't shared across calls). The factory itself stays a DI singleton; only the backends it produces are transient.

### 6. Declare manufacturer-mandated forbidden regions

If the backend's hardware should never fire on certain body regions per the manufacturer's safety guidance — TENS-class devices over the heart, devices contraindicated near the carotid, etc. — populate `ForbiddenRegions`:

```csharp
public IReadOnlySet<BodyRegion> ForbiddenRegions { get; } =
    ImmutableHashSet.Create(BodyRegion.ChestOverHeart, BodyRegion.Throat);
```

The bodymap validator refuses to register the backend at startup if a declared placement lands in any of these regions; this list is **non-overridable**. Backends that have no manufacturer-stated bans return `ImmutableHashSet<BodyRegion>.Empty` (both the mock and real OWO backends do — OWO's calibration ceiling handles intensity safety). See [`docs/body-map.md`](body-map.md) for the full taxonomy and the smited-default forbidden regions that every backend inherits.

### 7. Sensation library directory

Drop a `sensations/<your_kind>/*.json` directory at the repo root (or wherever `Smited:Sensations:LibraryRoot` points). At boot, `SensationLoader` picks up files whose `backend_kind` matches the backend's `Kind` field. Files with the default `scope: "kind"` bind to every backend instance of that kind; files with `scope: "id"` bind only to their `backend_id` and are skipped if that backend is absent. Schema validation (parameter types, ranges, required fields, zone IDs) runs against each target backend's `ParameterSchema` and `ZoneTopology` before the daemon finishes starting; a failing file aborts startup with the path and offending field.

If the backend should accept runtime registrations via the `RegisterSensation` RPC, advertise the `sensation_registry_mutable` capability tag. Runtime registrations are written to `LibraryRoot/<your_kind>/<name>.json` with `scope: "id"` and `backend_id` set to the backend that accepted the request — they survive across daemon restarts without leaking onto sibling backends of the same kind. Authored files can omit `scope` to keep the broader kind-level behavior.

## Platform-conditional backends (Windows-only example)

`Smited.Daemon.Owo` is the reference implementation — see [`docs/owo.md`](owo.md) for the user-facing setup, runbook, and TENS safety notes the OWO backend ships with. The structural pattern:

1. **`src/Smited.Daemon.<Platform>/<Platform>Backend.csproj`** — `<TargetFramework>net9.0-windows</TargetFramework>` (or whatever TFM). The `<PropertyGroup>` is **unconditional** — conditioning it produces an empty `TargetFramework` on the wrong platform and breaks the build.
2. The platform's NuGet package and the SDK-touching `<Compile Remove>` block go in conditional `<ItemGroup>`s gated on the `_TargetingWindows` MSBuild property (defined in `Directory.Build.props`):

   ```xml
   <ItemGroup Condition="'$(_TargetingWindows)' == 'true'">
     <PackageReference Include="MyVendor.Sdk" />
   </ItemGroup>

   <ItemGroup Condition="'$(_TargetingWindows)' != 'true'">
     <Compile Remove="MyBackend.cs" />
     <Compile Remove="StaticMySdk.cs" />
   </ItemGroup>
   ```

   Do **not** condition on `'$(OS)' == 'Windows_NT'` directly. `$(OS)` reflects the **build host**, not the **target**. Using it directly causes cross-publishes from Mac/Linux to `win-x64` (the Cake `Publish-Win-x64` task on CI) to omit the Windows-only backend, even though the binaries land on a Windows machine. `_TargetingWindows` evaluates true when either the host is Windows or the build was given a Windows runtime identifier (`-r win-x64` etc.), which is the correct semantic for "include the Windows-only assets." For other-OS backends, follow the same pattern with sibling properties (`_TargetingMac`, `_TargetingLinux`) defined in `Directory.Build.props`.
3. Set `<EnableWindowsTargeting>true</EnableWindowsTargeting>` on the backend csproj (unconditionally — the project only ever exists to target Windows). On the daemon csproj, set it gated on `_TargetingWindows` so non-Windows publishes don't get the irrelevant flag:

   ```xml
   <PropertyGroup Condition="'$(_TargetingWindows)' == 'true'">
     <EnableWindowsTargeting>true</EnableWindowsTargeting>
   </PropertyGroup>
   ```

   Without `EnableWindowsTargeting`, cross-publishing a `net9.0-windows` project from a non-Windows host fails because the Windows desktop SDK refuses to load.
4. The platform project references `Smited.Daemon.Abstractions` (so it can see `IHapticBackend` and any cross-platform helper types like `OwoBackendOptions`/`IOwoSdk`).
5. The daemon project's reverse `ProjectReference` is conditional on `_TargetingWindows` and uses `<ReferenceOutputAssembly>false</ReferenceOutputAssembly>` so the build graph stays acyclic. Daemon source never imports the backend type — `BackendsServiceCollectionExtensions.AddOwoBackendIfWindows` loads both `OwoBackendFactory` and `StaticOwoSdk` via `Type.GetType("Smited.Daemon.<Platform>.<Type>, Smited.Daemon.<Platform>")` at runtime, wrapped in a deliberately broad `catch (Exception)`: the recoverable set for reflective assembly loading is open-ended (`BadImageFormatException` for wrong-architecture DLLs, `FileNotFoundException` / `FileLoadException` for missing transitive deps, `TypeLoadException` / `ReflectionTypeLoadException` for type-resolution failures, `PlatformNotSupportedException` on a downlevel runtime, …) and they all mean the same thing: "backend unavailable here." Both types load atomically — if either fails, neither registers — so a partial install can't leave a factory whose dependencies aren't satisfied. Both the factory class and any auxiliary singletons it depends on (e.g. an `IOwoSdk` impl) must be `public sealed class` so cross-assembly reflective instantiation works.

## Tests

Add unit tests under `tests/Smited.Daemon.Tests/Backends/<YourKind>BackendTests.cs`:

- Static descriptors match your spec.
- Trigger emits Started → Completed across the estimated duration (use `FakeTimeProvider`).
- Stop emits Cancelled and returns the correct count.
- Calibration / status changes emit the matching events.

For end-to-end coverage with a real gRPC client, register your backend in `DaemonFixture` and add a test class under `tests/Smited.Daemon.Tests/EndToEnd/`.

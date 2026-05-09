# Smited.Daemon.Tests

End-to-end and unit tests for the smited daemon. Run the full suite from the repo root:

```sh
dotnet test
```

## Layout

- `Backends/` — `BackendRegistryTests`, `MockOwoBackendTests`. Verify the mock's static descriptors, lifecycle event emission, and concurrency contract.
- `Events/` — `EventBusTests`, `EventStreamTests`. Verify pub/sub fan-out, drop-oldest tolerance, and filter semantics directly against the bus.
- `Sensations/` — `SensationFileFormatTests`, `SensationLibraryTests`, `SensationLoaderTests`. JSON parser round-trips, in-memory store, boot-time loader validation.
- `Triggering/` — `TriggerCoordinatorTests`, `ConcurrencyPolicyTests`. Unit tests for the coordinator and the four concurrency policies (REJECT_NEW, CANCEL_OLDEST, PRIORITY, QUEUE).
- `EndToEnd/` — full-stack tests against an in-process host:
  - `CapabilityDiscoveryTests` — `ListBackends`, `DescribeBackend`, `Health` round-trip the mock.
  - `TriggerFlowTests` — accepted, unknown sensation, invalid zone, wrong parameter type, empty backend_id, trace echo.
  - `EventStreamTests` — Started→Completed via the gRPC stream, filter by kind, filter by backend.
  - `SensationLibraryE2ETests` — boot-loaded files, register/unregister round-trips, capability gating.
  - `PanicEndpointTests` — `/panic` over HTTP/1.1, no auth, GET and POST shapes.

## Determinism

`DaemonFixture` swaps `TimeProvider.System` for `FakeTimeProvider` so the mock backend's `Task.Delay` runs against a virtual clock. Tests advance time explicitly:

```csharp
_fixture.Time.Advance(TimeSpan.FromSeconds(1));
```

This keeps the suite milliseconds-fast.

## Fixtures and isolation

Each test class derives from `IDisposable` and constructs a fresh `DaemonFixture`, which spins up a `WebApplicationFactory<Program>`-hosted daemon, points the sensation library at a unique temp directory, and exposes typed gRPC + panic clients. Each test method gets its own fixture (xUnit constructs a fresh test-class instance per test).

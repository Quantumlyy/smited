# bHaptics integration

smited speaks the bHaptics Player WebSocket v2 protocol directly, without the official Unity or Unreal SDKs. The daemon is hardware-agnostic across the bHaptics device family (TactSuit X40, X16, Air, plus optional TactGloves and TactSleeves) and never carries a bHaptics developer App ID or API Key.

## Requirements

- A Windows host. The bHaptics Player is Windows-only; smited's bHaptics backend lives in `Smited.Daemon.Bhaptics`. The assembly itself is platform-portable — `BackendBootstrapper` only attempts to load it when `OperatingSystem.IsWindows()` is true.
- bHaptics Player installed and running on the same machine as the daemon.
- At least one device paired with the Player.
- The Player's WebSocket endpoint reachable at `ws://localhost:15881/v2/feedbacks` (the documented default; configurable via `Smited:Bhaptics:PlayerEndpoint`).

## Enabling

In `appsettings.json` or your user config:

```json
{
  "Smited": {
    "Backends": { "EnableBhaptics": true }
  }
}
```

Restart the daemon. The startup banner shows the `bhaptics-primary` backend in the registered list, alongside any other backends.

Configuration knobs (under `Smited:Bhaptics`):

| Key | Default | Notes |
|---|---|---|
| `BackendId` | `bhaptics-primary` | Identity advertised to gRPC clients. Override only when running multiple bHaptics backends against distinct devices on a single host (rare). |
| `PlayerEndpoint` | `ws://localhost:15881/v2/feedbacks` | Override only if the Player's port has been customised. |
| `MaxReconnectAttempts` | `3` | Reconnect attempts on Player disconnect, with exponential backoff (1s, 2s, 4s, ...). On success, status returns to `READY` and a `StatusChanged` lifecycle event with reason `reconnected` is emitted. After the limit the backend transitions to `BACKEND_STATUS_ERROR` with reason `reconnect_exhausted`. Set to `0` to skip reconnect entirely (immediate `ERROR` on disconnect). |
| `InitialStatusTimeoutMillis` | `1500` | How long `ConnectAsync` waits after the WebSocket handshake for the Player's first `deviceStatus` frame so any paired accessories are reflected in the topology before `SensationLoader` runs. Set to `0` to skip the wait — useful when the daemon owns the Player startup ordering. |

## Sensation authoring

bHaptics sensations live under `sensations/bhaptics_tactsuit/*.json` with `backend_kind: "bhaptics_tactsuit"`. Zone ids follow the `vest_front_N` (front, 0..19) and `vest_back_N` (back, 0..19) pattern, plus optional `glove_l_N` (0..5), `glove_r_N` (0..5), `arm_l_N` (0..3), `arm_r_N` (0..3) when accessories are present.

Zone groups available out of the box: `front`, `back`, `front_chest` (front 0..7), `back_shoulders` (back 0..3), `torso` (front + back), `all` (every registered motor — grows when accessories are paired). When accessories are reported by the Player, additional `gloves` and `arms` groups appear.

Required parameters per microsensation: `intensity` (0..100, %) and `duration` (0..10s). The Player's `dotMode` wire format carries only `(index, intensity)` per motor — there's nowhere on the wire for a frequency or other parameter, so the backend's schema is intentionally minimal. Frequency tuning, when supported by the device, is a Player-app concern.

See [`sensations/README.md`](../sensations/README.md) for the full file format reference and `Per-backend authoring notes` for how bHaptics interprets these parameters versus OWO.

### Accessories (TactSleeve, TactGlove)

The backend starts with the 40-zone vest topology. When the Player reports a paired TactSleeve or TactGlove via its periodic `deviceStatus` frame, the backend expands its advertised topology to include the corresponding zones (`arm_l_0..3`, `arm_r_0..3` for sleeves; `glove_l_0..5`, `glove_r_0..5` for gloves) plus the `arms` and `gloves` zone groups, and emits a `BackendLifecycleEvent` with `change = STATUS_CHANGED` so subscribed clients re-fetch via `DescribeBackend`. When an accessory disconnects, the topology collapses back to vest-only and emits a second `STATUS_CHANGED`.

Because sample sensations ship with `default_zone_ids` validated at boot against the *current* topology, files targeting accessory zones aren't included in `sensations/bhaptics_tactsuit/` by default — they'd abort startup on hosts without the accessory paired. Two ways to author sleeve/glove-targeting sensations:

1. Pair the accessory before authoring the file, then drop it in `sensations/bhaptics_tactsuit/<name>.json` and restart the daemon. `ConnectAsync` waits up to `InitialStatusTimeoutMillis` (default 1500ms) for the Player to push its first `deviceStatus` frame, so the topology reflects paired accessories before `SensationLoader` validates persisted files against it. If your Player is slow to push that frame, raise the timeout.
2. Use the `RegisterSensation` gRPC at runtime once the accessory is connected — `DescribeBackend` reports the expanded zones, `RegisterSensation` validates against the same, and the daemon persists the file with `scope: "id"` so it survives a restart for that backend instance only.

Face and shoes accessories are intentionally out of scope for v0.1.x; the topology and `ZoneIndexMap` would need extension to support them.

## Wire-protocol notes

smited only emits the `dotMode` pattern type — direct per-motor intensity arrays, one frame per microsensation, sequenced with `Task.Delay` between frames. PathMode (continuous coordinate interpolation) is reserved for future use; pre-authored bHaptics events (the App ID / API Key flow) are intentionally out of scope. This sidesteps the bHaptics developer portal entirely; sensations are authored in smited's own JSON format and compiled to dot patterns at trigger time.

The wire format DTOs in `src/Smited.Daemon.Bhaptics/WebSocket/Protocol.cs` mirror the haptic-library wiki documentation but have not been verified against a live Player on the development host. **On-hardware verification checklist:**

1. Run `wscat -c ws://localhost:15881/v2/feedbacks` while playing a sensation through an integrated game.
2. Capture the JSON frames the Player sends and receives.
3. Diff against `Protocol.cs` field names. Update any drift.
4. Run a smited `Trigger` on `bhaptics-primary` and confirm the suit fires the expected motors. If the daemon submits an apparently well-formed frame and the suit is silent, the most likely cause is a field-name mismatch between our DTOs and what the live Player accepts.

Until step 4 is signed off on real hardware, treat the bHaptics backend as fully wired but unverified end-to-end.

## Mac development

The Mac-runnable `MockBhapticsBackend` provides a faithful TactSuit X40 simulation for local development and the `tests/Smited.Daemon.Tests/Bhaptics/` test suite covers the protocol DTOs, `PlayerClient`, and `ZoneIndexMap` against an in-process Kestrel WebSocket simulator. Set `Smited:Backends:EnableMockBhaptics: true` to register the mock; you can pair it with `EnableMockOwo` and exercise multi-backend paths without any hardware.

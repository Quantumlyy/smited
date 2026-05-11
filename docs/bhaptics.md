# bHaptics integration

smited drives bHaptics hardware via the `Smited.Daemon.Bhaptics`
project (`src/Smited.Daemon.Bhaptics/`), which wraps the SDK1 NuGet
package `Bhaptics.Tac` (1.4.2). Three device families ship in v1:

| Device                | smited kinds                                 | Motor count |
| --------------------- | -------------------------------------------- | ----------- |
| TactSuit X40 (vest)   | `bhaptics_vest`                              | 40          |
| TactSleeve (per arm)  | `bhaptics_sleeve_l`, `bhaptics_sleeve_r`     | 6 each      |
| Tactosy for Feet      | `bhaptics_feet_l`, `bhaptics_feet_r`         | 3 each      |

Each kind is a separate descriptor so users can enable just the
devices they own. All five backends share a single `IBhapticsSdk`
singleton because the bHaptics Player is a per-host process that
owns every paired device.

This document covers prerequisites, configuration, the smoke-test
runbook, and notes on bHaptics vibrotactile safety practices.

## Prerequisites

- **Windows 10 or 11.** The bHaptics Player is Windows-only.
  Mac/Linux daemons run with the `mock_bhaptics_*` backends only —
  there is no shim available for real devices on those platforms.
- **bHaptics Player installed and running** in the background for the
  entire lifetime of the daemon. The SDK talks to the Player over a
  local WebSocket on port `15881`; the Player then holds the
  Bluetooth pairing with the device. Closing the Player drops the
  daemon→device transport.
- **Devices paired and calibrated** through the Player. Pairing is
  driven entirely by the Player UI. Per-device pairing is independent;
  you can pair just the vest, just the sleeves, just the feet, or
  any combination.

## First-time setup

1. Install bHaptics Player from `https://www.bhaptics.com/support/downloads`.
2. Pair each device through the Player's device-management UI
   (typically a Bluetooth pairing flow).
3. Test each device through the Player's built-in test patterns. If
   the test patterns don't reach the device, smited won't either.
4. Leave the Player running. The daemon's per-backend heartbeat polls
   the Player every `HeartbeatSeconds` (default 5 s) to detect drops.

## Enabling in smited

Edit your user config file (`%APPDATA%\smited\config.json`). The
descriptor list enables exactly the devices you own — there is no
default-on for bhaptics. The example below turns on all five real
backends; trim to the devices you actually have:

```json
{
  "Smited": {
    "Bhaptics": {
      "AppId": "smited"
    },
    "Backends": {
      "Items": [
        { "Kind": "bhaptics_vest", "Id": "bhaptics-vest", "Enabled": true },
        { "Kind": "bhaptics_sleeve_l", "Id": "bhaptics-sleeve-l", "Enabled": true },
        { "Kind": "bhaptics_sleeve_r", "Id": "bhaptics-sleeve-r", "Enabled": true },
        { "Kind": "bhaptics_feet_l", "Id": "bhaptics-feet-l", "Enabled": true },
        { "Kind": "bhaptics_feet_r", "Id": "bhaptics-feet-r", "Enabled": true }
      ]
    }
  }
}
```

`Smited:Bhaptics:AppId` is a daemon-wide setting because the SDK is a
process-wide singleton — the first `InitializeAsync` call wins, so
per-backend AppId would create silent ambiguity. The current
`Bhaptics.Tac` 1.4.2 does not actually accept an app identifier in
its constructor, so this value lives only in smited's logs today; the
field is wired now so a future SDK upgrade has a single bound config
source.

Per-backend `Options` (currently just `BackendId`, `HeartbeatSeconds`,
`MaxReconnectAttempts`) is available but rarely needed — the defaults
match what the Player expects:

```json
{
  "Kind": "bhaptics_vest",
  "Id": "bhaptics-vest",
  "Enabled": true,
  "Options": {
    "HeartbeatSeconds": 5,
    "MaxReconnectAttempts": 3
  }
}
```

## Smoke-test runbook

Restart the daemon after editing config. The startup banner shows
each enabled bhaptics descriptor as a registered backend.

```sh
grpcurl -plaintext localhost:7777 smited.v1.SmitedService/Health
# Expected: backends list includes each enabled bhaptics-* id.

grpcurl -plaintext -d '{"backend_id":"bhaptics-vest"}' \
    localhost:7777 smited.v1.SmitedService/DescribeBackend
# Expected: zones include cross-backend portable IDs (pectoral_l/r,
# abdominal_l/r, lumbar_l/r, dorsal_l/r) plus bhaptics_vest_* device-
# specific zones; parameters cover intensity / duration / ramp_up /
# ramp_down / exit_delay — NO frequency (bHaptics is vibrotactile).

grpcurl -plaintext -d '{
    "backend_id":"bhaptics-vest",
    "sensation_name":"compile_error_severe"
}' localhost:7777 smited.v1.SmitedService/Trigger
# Expected: accepted=true. You feel a sharp vibration across both
# pectoral zones on the vest.
```

Iterate the sample library:

| Sensation              | Backend                              | Expected feel                         |
| ---------------------- | ------------------------------------ | ------------------------------------- |
| `compile_error_mild`   | `bhaptics-vest`                      | Firm bump on left pectoral            |
| `compile_error_severe` | `bhaptics-vest`                      | Sharp jab across both pectorals       |
| `test_failed`          | `bhaptics-vest`                      | Two-pulse abdomen pattern             |
| `deploy_success`       | `bhaptics-vest`                      | Gentle ramped wave across upper back  |
| `chat_tap`             | `bhaptics-sleeve-l`                  | Soft tap on the left arm              |
| `chat_zap`             | `bhaptics-sleeve-l`                  | Sharp zap on the left arm             |
| `pager_alert`          | `bhaptics-feet-l`                    | Two short pulses under the left foot  |

## Device-to-kind mapping

bHaptics SDK identifies devices by a `PositionType` enum that
includes vest, arm sleeves (left/right), gloves (left/right), feet
(left/right), head, etc. v1 covers the three families above. Future
devices (TactGlove, TactVisor, TactGo) follow the same template as
additional kinds in a follow-up.

`StaticBhapticsSdk.MapDeviceKey` translates smited's vocabulary
(`vest | sleeve_l | sleeve_r | feet_l | feet_r`) into the SDK's
`PositionType` enum. The mapping is fixed at compile time — there is
no user knob.

## Motor layout

Refer to the bHaptics SDK documentation
(`https://docs.bhaptics.com`) for the canonical motor-numbering
diagrams. smited's zone→motor mapping lives in `BhapticsMotorMap`
(`src/Smited.Daemon.Abstractions/Backends/BhapticsMotorMap.cs`):

- **TactSuit X40**: motors 0–19 are the 20 front actuators, 20–39
  are the back. Cross-backend portable zones partition the 40 motors
  into 8 quadrants (one motor in exactly one quadrant); device-
  specific `bhaptics_vest_*` zones cover narrower bands.
- **TactSleeve**: 6 motors per arm running wrist (0) → bicep (5).
  `arm_{l,r}` covers all six; `bhaptics_sleeve_{wrist,forearm,elbow,bicep}_{l,r}`
  isolate sub-regions.
- **Tactosy for Feet**: 3 motors per foot ordered heel (0) → arch (1)
  → toes (2). `foot_{l,r}` covers all three; per-region zones isolate.

## Forbidden regions

The vest backend declares manufacturer-mandated forbidden regions
covering the head/face/throat/neck (no actuators there to begin with)
plus the chest-over-heart region per general vibrotactile safety
guidance. The smited bodymap validator refuses any placement that
declares those regions for a `bhaptics_vest` backend. Sleeve and feet
backends declare no forbidden regions — arm and foot vibration has
no specific bans.

The smited daemon-wide defaults (face, throat, pelvis,
chest-over-heart) also apply on top; opt out per-region via
`Smited:BodyMap:AllowOverrideRegions`. See
[`docs/body-map.md`](body-map.md).

## Troubleshooting

**`accepted=true` but no vibration**
1. Player still showing "Connected" for the device? If not, re-pair
   from the Player UI.
2. Run the Player's built-in test pattern. If the Player can drive
   the device, smited can; if it can't, the issue is in the Player
   layer.
3. Battery? Devices report low-battery state in the Player UI.

**Daemon banner shows the backend but `Health` reports `DISCONNECTED`**
- Device-level transport drop detected by the heartbeat poll. Check
  the Player still owns the device pairing. The daemon polls per-
  device connectivity with exponential backoff up to
  `MaxReconnectAttempts`; on exhaustion `Status` flips to `ERROR`
  and you must restart the daemon.

**Daemon banner doesn't show the backend at all on Windows**
- Confirm the descriptor exists with `Enabled: true`. Check that
  `Smited.Daemon.Bhaptics.dll` and `Bhaptics.Tact.dll` are in the
  daemon's output directory — the daemon project copies them as
  content on Windows builds. If both are present but the banner
  still shows no bhaptics backend, check the log for `bHaptics
  assembly load failed` from `AddBhapticsBackendIfWindows`; common
  causes are wrong-architecture binaries or a missing transitive.

**Sensation file declares `frequency` and SensationLoader aborts boot**
- bHaptics is vibrotactile and the parameter schema does not declare
  `frequency`. Sensation files cloned from `sensations/owo_skin/`
  must have `frequency` removed before they're placed under
  `sensations/bhaptics_*/`. See the shipped files under
  `sensations/bhaptics_vest/` for the expected shape.

## Panic button

The `/panic` HTTP endpoint cancels every active sensation across
every backend, including each bhaptics device. Use this from a
Stream Deck button:

```
POST http://127.0.0.1:7778/panic
```

Internally each bhaptics backend's `StopAsync(All=true)` runs
`Sdk.StopDevice(deviceKey)` for its own device — the shared SDK
singleton is NOT torn down, so other backends keep working through
the same `HapticPlayer` instance.

## Safety notes

bHaptics devices are vibrotactile — they vibrate, they do not deliver
electrical stimulation. The published safety considerations are
considerably less strict than OWO's EMS, but common-sense practices
apply:

- Don't run high-intensity continuous patterns over the chest-over-
  heart region. smited's vest backend declares this region as
  manufacturer-forbidden; placing it triggers a bodymap validation
  error at boot.
- Stop if the device feels uncomfortably warm — the actuators can
  warm slightly during sustained use.
- Battery-powered devices have a duty cycle. Don't expect a vest to
  drive 100%-intensity patterns continuously for hours on end without
  thermal throttling or battery exhaustion.

smited's `intensity` parameter is interpreted as a percentage 0–100
of motor maximum. There is no per-user calibration step — the
maximum is a device property, not a user-specific safety boundary
the way OWO's calibrated maximum is.

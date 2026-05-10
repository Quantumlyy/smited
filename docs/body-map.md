# Body map

The body map is a daemon-internal layer that knows where each registered backend's haptic zones sit on the user's body. It runs at startup and on every trigger; it is never exposed over the wire (the v0.1.0 schema does not describe placements). When configured, it does three things:

1. Refuses backends whose declared placements land in **manufacturer-mandated forbidden regions**.
2. Refuses placements that land in smited's own **conservative default forbidden regions** (Face, Throat, Pelvis, ChestOverHeart) unless the user explicitly opts out.
3. Optionally rejects triggers whose resolved zones cross **regions covered by another backend** when `OverlapPolicy` is `Refuse`.

The bodymap is opt-in. With no `Smited:BodyMap` configuration the daemon runs in "unmapped mode" — the banner reads `Body map  Not configured (warnings off)` and every check is a no-op.

## Configuration

Lives under `Smited:BodyMap` in `appsettings.json` or the user-config file:

```json
{
  "Smited": {
    "BodyMap": {
      "OverlapPolicy": "Warn",
      "AllowOverrideRegions": [],
      "Placements": [
        {
          "BackendId": "mock-owo",
          "ZoneIds": ["pectoral_l", "pectoral_r"],
          "Region": "ChestFront",
          "Description": "main vest, front of torso"
        },
        {
          "BackendId": "mock-owo",
          "ZoneIds": ["dorsal_l", "dorsal_r"],
          "Region": "BackUpper"
        }
      ]
    }
  }
}
```

Each placement maps **one backend's zones** onto **one body region**. A backend whose zones span multiple regions appears as multiple placement entries — one per region. `ZoneIds` may include zone-group ids (e.g. OWO's `arms` group); the validator expands them to the underlying leaf zones.

`Description` is free-form and exists for the user's own bookkeeping; the daemon ignores it.

## Overlap policy

When two backends declare placements that cover the same region, the configured `OverlapPolicy` decides what happens:

| Policy | Startup behaviour | Trigger behaviour |
|---|---|---|
| `Warn` (default) | Logs a `WARN` line for every overlapping region | No effect; triggers proceed |
| `Refuse` | Same warning | `TriggerCoordinator` rejects any trigger whose resolved zones cross an overlapping region with `INVALID_ZONE` |
| `Off` | No warning | No effect |

Use `Warn` when overlap is ambient information, `Refuse` when overlapping fire could physically interfere (e.g. two TENS-class devices on the same skin region), and `Off` when overlap is intentional (layering haptic categories).

## Forbidden regions

Two distinct rule sources, with different override semantics:

- **Manufacturer-mandated** — declared by the backend itself via `IHapticBackend.ForbiddenRegions`. Non-overridable. The daemon refuses to register a backend whose declared placement lands in one of these regions, regardless of user configuration. Sample backends today (mock OWO, real OWO) declare an empty set.
- **Smited's defaults** — the daemon ships with `{Face, Throat, Pelvis, ChestOverHeart}` blocked for every backend. The user can opt out individually:

  ```json
  "AllowOverrideRegions": ["Pelvis"]
  ```

  This relaxes only smited's defaults — manufacturer bans still hold.

The forbidden-region check walks **up the hierarchy** so subregion declarations cannot bypass parent-region bans. Today the only parent-child relationship is `ChestOverHeart ⊂ ChestFront`: declaring a backend in `ChestFront` exposes it to `ChestOverHeart`'s defaults, and a backend that bans `ChestFront` also bans `ChestOverHeart`.

## Region taxonomy

The enum `Smited.Daemon.BodyMap.BodyRegion` covers ~30 named regions. They are deliberately coarse — at hardware-addressability granularity, not anatomical precision. New backends with finer-grained needs (e.g. a future facial accessory) extend the taxonomy in a versioned daemon release.

```
                       ┌──────────┐
                       │   Head   │  Face, Head
                       └────┬─────┘
                            │
                       ┌────┴─────┐
                       │  Throat  │
                       │   Neck   │
                       └────┬─────┘
                            │
       LeftShoulder ───┬────┼────┬─── RightShoulder
                       │    │    │
       LeftUpperArm ───┤  ChestFront ┤── RightUpperArm
                       │  ├ ChestOverHeart
       LeftForearm  ───┤    │    ├── RightForearm
                       │    │    │
       LeftWrist    ───┤ AbdomenU ┤── RightWrist
       LeftHand     ───┤ AbdomenL ┤── RightHand
                       │    │    │
                       │  Pelvis │
                       │   /  \  │
       LeftThigh    ───┴────┴────┴── RightThigh
       LeftKnee     ──────────────── RightKnee
       LeftCalf     ──────────────── RightCalf
       LeftAnkle    ──────────────── RightAnkle
       LeftFoot     ──────────────── RightFoot

       (back) BackUpper, BackLower, Glutes
```

`Unspecified` (the enum's zero value) is reserved for "not part of the body map" and is silently ignored by every check.

## Worked example: OWO + a fictional bhaptics overlap on the chest

```json
{
  "Smited": {
    "Backends": {
      "Items": [
        { "Kind": "owo_skin", "Id": "owo-primary", "Enabled": true },
        { "Kind": "bhaptics_tactsuit", "Id": "tactsuit", "Enabled": true }
      ]
    },
    "BodyMap": {
      "OverlapPolicy": "Refuse",
      "Placements": [
        {
          "BackendId": "owo-primary",
          "ZoneIds": ["pectoral_l", "pectoral_r"],
          "Region": "ChestFront"
        },
        {
          "BackendId": "tactsuit",
          "ZoneIds": ["chest_left", "chest_right"],
          "Region": "ChestFront"
        }
      ]
    }
  }
}
```

Startup: both backends register, the validator emits one warning (`Region 'ChestFront' is covered by 2 backends: owo-primary, tactsuit`), and the banner reads `Body map  2 placements, 1 warning`. With `OverlapPolicy: "Refuse"`, the next gRPC trigger that resolves to `pectoral_l` on `owo-primary` returns `accepted=false` with `INVALID_ZONE` and a message naming `tactsuit` as the conflicting backend. Switch the policy to `Warn` (or remove it; `Warn` is the default) and the same trigger proceeds normally.

## Banner

The startup banner gains two pieces of information:

```
╭─smited──────────────────────────────────────────────╮
│ Listening   gRPC 127.0.0.1:7777 (h2c, reflection on)│
│ Panic       POST http://127.0.0.1:7778/panic        │
│ Backends    2 registered (1 refused)                │
│ Body map    3 placements, 1 warning                 │
│ Sensations  10 loaded                               │
╰─────────────────────────────────────────────────────╯
```

`(N refused)` appears only when the bodymap deregistered backends. `Body map` reads `Not configured (warnings off)` in grey when no placements are declared.

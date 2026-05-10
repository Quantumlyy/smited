# Sensation library

JSON files under this directory describe pre-registered sensations the daemon loads at boot. Each subdirectory is named after the backend `kind` it targets — `owo_skin/` for files that should bind to OWO Skin backends.

## File format

```json
{
  "name": "compile_error_severe",
  "backend_kind": "owo_skin",
  "scope": "kind",
  "display_name": "Compile Error (Severe)",
  "description": "Strong jab to both pectorals.",
  "tags": ["build", "error", "severe"],
  "default_zone_ids": ["pectoral_l", "pectoral_r"],
  "default_intensity": 80,
  "estimated_duration": "0.6s",
  "definition": {
    "microsensations": [
      {
        "parameters": {
          "frequency":  { "number":   100 },
          "intensity":  { "number":   80 },
          "duration":   { "duration": "0.4s" },
          "ramp_up":    { "duration": "0.05s" },
          "ramp_down":  { "duration": "0.15s" }
        }
      }
    ]
  }
}
```

### Keys

- `name` — library key. Lowercase ident shape (`^[a-z0-9][a-z0-9_-]*$`).
- `backend_kind` — directory-level binding to a backend family (e.g. `owo_skin`). The file must live under the matching `sensations/<backend_kind>/` directory.
- `scope` — optional. `"kind"` (default) binds the file to **every** registered backend whose `Kind` matches. `"id"` binds it only to `backend_id`.
- `backend_id` — required when `scope` is `"id"`. If that backend is absent at startup, the loader skips the file.
- `display_name` — human-readable label for UIs.
- `description` — free text, surfaces in tooling.
- `tags` — free-form short tokens used by `ListSensations` filtering.
- `default_zone_ids` — applied when a `Trigger` doesn't override zones. Each id must exist on the target backend (zone or zone group).
- `default_intensity` — 0–100, applied as an `intensity_scale` when `Trigger` doesn't override.
- `estimated_duration` — authoring-time hint, accepts `"0.6s"` or `"600ms"` shapes.
- `definition.microsensations` — non-empty array. Each entry is a parameter map keyed by the backend's `ParameterDef.Name`.

### `ParameterValue` oneof

Exactly one of these keys must be set per parameter value:

- `{ "number": 100 }` — for `PARAMETER_TYPE_NUMBER`.
- `{ "bool_value": true }` — for `PARAMETER_TYPE_BOOL`.
- `{ "string_value": "foo" }` — for `PARAMETER_TYPE_STRING`.
- `{ "duration": "0.4s" }` — for `PARAMETER_TYPE_DURATION`.
- `{ "enum_value": "ramp_linear" }` — for `PARAMETER_TYPE_ENUM`.

The loader rejects ambiguous shapes (multiple variants set, none set) at boot. The daemon aborts startup with the offending file path and JSON path in the error message.

## Validation

Each file is validated at startup against the matching backend's `ParameterSchema` and `ZoneTopology`:

- Every required parameter must be present.
- Every parameter type must match the backend's `ParameterDef.Type`.
- Numeric and duration values must fall within the backend's `min`/`max`.
- Enum values must appear in `enum_values`.
- Every `default_zone_ids` entry must be a known zone or zone group on the backend.

A failing file aborts the daemon's start; fix it and restart.

## Adding a new sensation

1. Drop a `.json` file under `sensations/<backend_kind>/`.
2. Restart the daemon.
3. Verify with `ListSensations`:
   ```sh
   grpcurl -plaintext localhost:7777 smited.v1.SmitedService/ListSensations
   ```

Runtime registration via `RegisterSensation` is also supported by backends advertising the `sensation_registry_mutable` capability — check `DescribeBackend` to confirm. Runtime-registered files are written with `scope: "id"` and `backend_id` so they reload only for the backend that accepted the registration.

## Per-backend authoring notes

The two shipping backends interpret parameters differently. Author files in `sensations/<backend_kind>/` matching the kind you target — the loader binds the file to every registered backend whose `Kind` matches.

### OWO Skin (`sensations/owo_skin/`)

- TENS / EMS-based stimulation. `intensity` is a percentage of the user's calibrated pain threshold, not a raw amplitude — the same value will feel different across users. Sensations should advertise an `estimated_duration` that includes any `ramp_up`/`ramp_down`/`exit_delay` so the daemon's concurrency slot stays held for the right wall-clock window.
- Concurrency is exclusive — `CONCURRENCY_POLICY_CANCEL_OLDEST` with `max_concurrent = 1`. A new trigger preempts the active sensation; chained pulses must live inside one sensation's `microsensations` array, not as separate triggers.
- Required parameters per microsensation: `frequency` (Hz), `intensity` (%), `duration`. Optional envelope shapers: `ramp_up`, `ramp_down`, `exit_delay`.

### bHaptics TactSuit (`sensations/bhaptics_tactsuit/`)

- Vibration motors. `intensity` (0–100) maps directly to per-motor PWM, modulated by the bHaptics Player app's global slider — there's no per-user calibration step. Motors physically sum on the device, so concurrent sensations can stack.
- Concurrency is `CONCURRENCY_POLICY_PRIORITY` with `max_concurrent = 4`. Higher-priority triggers preempt lower-priority ones; equal-priority within capacity stack normally.
- Required parameters per microsensation: `intensity` (%) and `duration`. The Player's `dotMode` wire protocol carries only `(motor_index, intensity)`, so no other parameters are accepted.
- Zone groups follow the canonical bHaptics layout: `front` and `back` cover the 20 motors of each vest half; `front_chest` and `back_shoulders` are the top rows; `torso` is both halves; `all` is every registered motor (and grows when accessories are attached). Front and back zones use distinct `Frame` labels (`body_front` / `body_back`) so cross-backend clients can tell which side of the body they target.

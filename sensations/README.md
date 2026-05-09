# Sensation library

JSON files under this directory describe pre-registered sensations the daemon loads at boot. Each subdirectory is named after the backend `kind` it targets — `owo_skin/` for files that should bind to OWO Skin backends.

## File format

```json
{
  "name": "compile_error_severe",
  "backend_kind": "owo_skin",
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

- `name` — library key, unique within the file's `backend_kind`. Lowercase ident shape (`^[a-z0-9][a-z0-9_-]*$`).
- `backend_kind` — file-level binding to a backend family (e.g. `owo_skin`). The loader binds the sensation to **every** registered backend whose `Kind` matches, so one file works against any number of identically-typed backends.
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

Runtime registration via `RegisterSensation` is also supported by backends advertising the `sensation_registry_mutable` capability — check `DescribeBackend` to confirm.

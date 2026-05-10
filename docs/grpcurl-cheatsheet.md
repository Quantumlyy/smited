# grpcurl cheatsheet

Every example assumes the daemon is running with reflection enabled, so no `.proto` file is needed. Install grpcurl via `brew install grpcurl` if you don't have it.

## Discover the service

```sh
grpcurl -plaintext localhost:7777 list
# grpc.reflection.v1.ServerReflection
# grpc.reflection.v1alpha.ServerReflection
# smited.v1.SmitedService

grpcurl -plaintext localhost:7777 list smited.v1.SmitedService
# smited.v1.SmitedService.DescribeBackend
# smited.v1.SmitedService.Health
# smited.v1.SmitedService.ListBackends
# smited.v1.SmitedService.ListSensations
# smited.v1.SmitedService.RegisterSensation
# smited.v1.SmitedService.Stop
# smited.v1.SmitedService.SubscribeEvents
# smited.v1.SmitedService.Trigger
# smited.v1.SmitedService.UnregisterSensation

grpcurl -plaintext localhost:7777 describe smited.v1.SmitedService
```

## Health

```sh
grpcurl -plaintext localhost:7777 smited.v1.SmitedService/Health
```

Returns `daemon_running`, `started_at`, `version`, and the current `backends` list.

## Capability discovery

```sh
# All registered backends
grpcurl -plaintext localhost:7777 smited.v1.SmitedService/ListBackends

# Filter by capability tag (intersection — every tag must be present)
grpcurl -plaintext -d '{"with_capabilities":["ems","calibrated"]}' \
  localhost:7777 smited.v1.SmitedService/ListBackends

# Full description of one backend (zones, parameters, concurrency, calibration)
grpcurl -plaintext -d '{"backend_id":"mock-owo"}' \
  localhost:7777 smited.v1.SmitedService/DescribeBackend
```

## Trigger

By library name (uses the backend's defaults for zones/intensity):

```sh
grpcurl -plaintext -d '{"sensation_name":"compile_error_mild","backend_id":"mock-owo"}' \
  localhost:7777 smited.v1.SmitedService/Trigger
```

With overrides:

```sh
grpcurl -plaintext -d '{
  "sensation_name": "compile_error_severe",
  "backend_id": "mock-owo",
  "zone_ids": ["pectoral_l"],
  "intensity_scale": 90,
  "priority": 10,
  "client_trace_id": "ci-build-1234"
}' localhost:7777 smited.v1.SmitedService/Trigger
```

Inline (no library lookup):

```sh
grpcurl -plaintext -d '{
  "backend_id": "mock-owo",
  "inline": {
    "microsensations": [{
      "parameters": {
        "frequency": { "number": 60 },
        "intensity": { "number": 70 },
        "duration":  { "duration": "0.5s" }
      }
    }]
  },
  "zone_ids": ["abdominal_l", "abdominal_r"]
}' localhost:7777 smited.v1.SmitedService/Trigger
```

A successful trigger returns `accepted=true` plus a `sensation_id`; a domain rejection returns `accepted=false` with `error.code` set (e.g. `TRIGGER_ERROR_CODE_SENSATION_NOT_FOUND`, `TRIGGER_ERROR_CODE_INVALID_ZONE`, `TRIGGER_ERROR_CODE_INVALID_PARAMETER`) and `error.field` pointing at the offending input.

## Stop

```sh
# One specific sensation
grpcurl -plaintext -d '{"sensation_id":"<the id>"}' \
  localhost:7777 smited.v1.SmitedService/Stop

# Every sensation on one backend
grpcurl -plaintext -d '{"backend_id":"mock-owo"}' \
  localhost:7777 smited.v1.SmitedService/Stop

# Everything everywhere
grpcurl -plaintext -d '{"all":true}' \
  localhost:7777 smited.v1.SmitedService/Stop
```

For the human-driven panic path, prefer the dedicated HTTP endpoint:

```sh
curl -X POST http://localhost:7778/panic
```

## Sensation library

```sh
# All sensations
grpcurl -plaintext localhost:7777 smited.v1.SmitedService/ListSensations

# Narrow by backend and tags (intersection)
grpcurl -plaintext -d '{"backend_id":"mock-owo","tags":["build","error"]}' \
  localhost:7777 smited.v1.SmitedService/ListSensations
```

Register at runtime (only on backends advertising `sensation_registry_mutable`):

```sh
grpcurl -plaintext -d '{
  "sensation": {
    "name": "custom_ping",
    "backend_id": "mock-owo",
    "display_name": "Custom Ping",
    "tags": ["personal"],
    "default_zone_ids": ["arm_l"],
    "default_intensity": 50,
    "estimated_duration": "0.2s",
    "definition": {
      "microsensations": [{
        "parameters": {
          "frequency": { "number": 80 },
          "intensity": { "number": 50 },
          "duration":  { "duration": "0.2s" }
        }
      }]
    }
  }
}' localhost:7777 smited.v1.SmitedService/RegisterSensation
```

Unregister:

```sh
grpcurl -plaintext -d '{"backend_id":"mock-owo","name":"custom_ping"}' \
  localhost:7777 smited.v1.SmitedService/UnregisterSensation
```

## Subscribe to events

Server-streaming RPC; cancel with Ctrl-C:

```sh
# Every event
grpcurl -plaintext -d '{}' localhost:7777 smited.v1.SmitedService/SubscribeEvents

# Just sensation lifecycle
grpcurl -plaintext -d '{
  "kinds": ["EVENT_KIND_SENSATION_STARTED","EVENT_KIND_SENSATION_COMPLETED","EVENT_KIND_SENSATION_CANCELLED"]
}' localhost:7777 smited.v1.SmitedService/SubscribeEvents

# Just one backend
grpcurl -plaintext -d '{"backend_ids":["mock-owo"]}' \
  localhost:7777 smited.v1.SmitedService/SubscribeEvents
```

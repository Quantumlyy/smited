# History database

smited records every trigger, stop, panic, and lifecycle event to a SQLite database for post-hoc queries. The database is daemon-internal — not exposed via gRPC. A future admin UI is the planned read consumer.

## Location

- **macOS / Linux**: `${XDG_DATA_HOME:-~/.local/share}/smited/history.db`
- **Windows**: `%LOCALAPPDATA%\smited\history.db`

Override via `Smited.History.CustomPath` in your user config. The startup banner prints the resolved path on every run.

## Tables

- `Triggers` — one row per gRPC `Trigger` call, accepted or rejected. Includes resolved zone ids (JSON-encoded), client trace id, and on rejections the proto error code and field path.
- `Stops` — one row per stop event. `Source` is `"grpc"` (deliberate gRPC `Stop`) or `"panic"` (the dedicated HTTP endpoint).
- `Panics` — one row per `/panic` invocation, with peer IP and user-agent.
- `Lifecycle` — sensation lifecycle (`SensationStarted`, `SensationCompleted`, `SensationCancelled`), calibration changes, and registry register/unregister events.
- `BackendStates` — backend register/deregister/status-change events with backend metadata captured at the time.

## Querying

Plain SQLite — query directly:

```sh
sqlite3 ~/.local/share/smited/history.db
> .tables
> SELECT * FROM Triggers ORDER BY Timestamp DESC LIMIT 10;
> SELECT COUNT(*) FROM Panics WHERE Timestamp > datetime('now', '-1 day');
> SELECT SensationName, COUNT(*)
    FROM Triggers
    WHERE Accepted = 1 AND Timestamp > datetime('now', '-7 days')
    GROUP BY SensationName ORDER BY 2 DESC;
> SELECT EventKind, COUNT(*) FROM Lifecycle GROUP BY EventKind;
```

## Retention

Default: 30 days. Configurable via `Smited.History.RetentionDays` in user config. Set to `0` to keep history forever (database grows unbounded — fine for low-volume personal use).

A background `HistoryRetentionService` runs once per day, deletes rows past the cutoff, and runs `VACUUM` weekly to reclaim space. If a pass fails it logs at warning level and waits for the next interval.

## Disable

Set `Smited.History.Enabled = false` in your user config. The daemon registers a no-op recorder and skips creating or opening the database. Every trigger and event still works, just unrecorded.

## Failure isolation

History writes are best-effort. If the database file is locked, corrupt, or inaccessible, smited logs a warning at `Warning` level and keeps running. The daemon's hot path is never coupled to history availability — recorder calls are fire-and-forget on the gRPC and panic call sites.

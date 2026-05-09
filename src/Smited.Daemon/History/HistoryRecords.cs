namespace Smited.Daemon.History;

/// <summary>
/// One row per gRPC <c>Trigger</c> call, recording both accepted and
/// rejected attempts. <see cref="ZoneIdsJson"/> stores the resolved zone
/// list (after sensation defaults are applied) as JSON to keep the schema
/// flat without needing a side table.
/// </summary>
internal sealed class TriggerRecord
{
    public long Id { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    public string BackendId { get; set; } = "";

    /// <summary>
    /// Library name when the trigger referenced a registered sensation,
    /// <c>null</c> for inline sensations.
    /// </summary>
    public string? SensationName { get; set; }

    /// <summary>
    /// Daemon-assigned sensation id (non-empty when accepted, empty
    /// otherwise).
    /// </summary>
    public string SensationId { get; set; } = "";

    /// <summary>JSON-encoded array of resolved zone ids.</summary>
    public string ZoneIdsJson { get; set; } = "[]";

    public uint? IntensityScale { get; set; }

    public int Priority { get; set; }

    public string ClientTraceId { get; set; } = "";

    public bool Accepted { get; set; }

    /// <summary>
    /// Proto <c>TriggerErrorCode</c> name when <see cref="Accepted"/> is
    /// false. <c>null</c> on accepted triggers.
    /// </summary>
    public string? ErrorCode { get; set; }

    /// <summary>Field path of the offending input, if available.</summary>
    public string? ErrorField { get; set; }
}

/// <summary>
/// One row per stop event, regardless of source. <see cref="Source"/>
/// distinguishes <c>"grpc"</c> (a deliberate gRPC <c>Stop</c> call) from
/// <c>"panic"</c> (the dedicated HTTP endpoint).
/// </summary>
internal sealed class StopRecord
{
    public long Id { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    public string Source { get; set; } = "";

    public string? SensationId { get; set; }

    public string? BackendId { get; set; }

    public bool All { get; set; }

    public int StoppedCount { get; set; }
}

/// <summary>
/// One row per <c>/panic</c> invocation. Always paired with a
/// corresponding <see cref="StopRecord"/> with <c>Source="panic"</c>.
/// </summary>
internal sealed class PanicRecord
{
    public long Id { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    public string Peer { get; set; } = "";

    public string UserAgent { get; set; } = "";

    public bool Ok { get; set; }

    public int StoppedCount { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// One row per sensation lifecycle event observed on the
/// <see cref="Smited.Daemon.Events.EventBus"/>. Captures <c>SensationStarted</c>,
/// <c>SensationCompleted</c>, <c>SensationCancelled</c> plus calibration
/// and registry changes.
/// </summary>
internal sealed class LifecycleRecord
{
    public long Id { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    public string BackendId { get; set; } = "";

    /// <summary>Proto <c>EventKind</c> name (e.g. <c>SensationStarted</c>).</summary>
    public string EventKind { get; set; } = "";

    public string? SensationId { get; set; }

    public string? SensationName { get; set; }

    public string? ClientTraceId { get; set; }

    public string? Reason { get; set; }
}

/// <summary>
/// One row per backend register/deregister/status-change event. Useful
/// for diagnosing "the daemon thinks the vest is disconnected, but the
/// vest claims it's online" disputes after the fact.
/// </summary>
internal sealed class BackendStateRecord
{
    public long Id { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    public string BackendId { get; set; } = "";

    public string Kind { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string Status { get; set; } = "";

    /// <summary><c>"registered"</c>, <c>"deregistered"</c>, or <c>"status_changed"</c>.</summary>
    public string Event { get; set; } = "";

    public string? Reason { get; set; }
}

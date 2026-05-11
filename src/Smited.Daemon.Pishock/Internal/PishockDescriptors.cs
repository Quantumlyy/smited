using System.Collections.Immutable;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.BodyMap;
using Smited.V1;

namespace Smited.Daemon.Pishock.Internal;

/// <summary>
/// Static descriptor builders shared by the mock and real PiShock
/// backends — every PiShock instance ships the same zone topology
/// (one zone per device), the same forbidden-region set, and the
/// same parameter schema modulo the per-descriptor
/// <c>AllowedOps</c> filter on the <c>op</c> enum's allowed values.
/// </summary>
internal static class PishockDescriptors
{
    /// <summary>
    /// Manufacturer-mandated forbidden regions: the published "neck,
    /// spine, chest" rule plus the conservative addition of
    /// <see cref="BodyRegion.Head"/>/<see cref="BodyRegion.Face"/>
    /// (electrical stimulation above the neck is universally bad).
    /// Surfaced as <c>IHapticBackend.ForbiddenRegions</c>; the bodymap
    /// validator's existing non-overridable mechanism rejects any
    /// placement that lands here regardless of
    /// <c>AllowOverrideRegions</c>.
    /// </summary>
    public static IReadOnlySet<BodyRegion> ManufacturerForbiddenRegions { get; } =
        ImmutableHashSet.Create(
            BodyRegion.Head,
            BodyRegion.Face,
            BodyRegion.Throat,
            BodyRegion.Neck,
            BodyRegion.ChestFront,
            BodyRegion.ChestOverHeart,
            BodyRegion.BackUpper,
            BodyRegion.BackLower);

    public static IReadOnlyList<string> BuildCapabilities(IReadOnlyList<PishockOp> allowed)
    {
        var caps = new List<string> { "pishock" };
        if (allowed.Contains(PishockOp.Vibrate)) caps.Add("vibrate");
        if (allowed.Contains(PishockOp.Beep)) caps.Add("beep");
        if (allowed.Contains(PishockOp.Shock)) caps.Add("shock");
        caps.Add("ratelimited");
        return caps;
    }

    public static ZoneTopology BuildZones()
    {
        var topology = new ZoneTopology();
        topology.Zones.Add(new Zone
        {
            Id = "shock",
            DisplayName = "Shock zone",
            // Single-zone device — position is illustrative only; the
            // body map's BackendId+Region binding is what places the
            // shocker on the user's body, not this hint.
            Position = new PositionHint { X = 0.5f, Y = 0.5f, Z = 0.5f, Frame = "device" },
        });
        return topology;
    }

    public static ParameterSchema BuildParameters(IReadOnlyList<PishockOp> allowed)
    {
        var schema = new ParameterSchema();

        var opDef = new ParameterDef
        {
            Name = "op",
            Type = ParameterType.Enum,
            Required = true,
            Description = "PiShock operation type — vibrate, beep, or shock",
        };
        // The schema advertises every PiShock op regardless of the
        // descriptor's AllowedOps — narrowing here would refuse
        // kind-scoped bundled sensations targeting Beep on a
        // vibrate-only descriptor at startup, which is far worse than
        // letting them load and rejecting the trigger if fired. The
        // per-instance AllowedOps gate runs at trigger time in
        // PishockTriggerValidator with a structured INVALID_PARAMETER.
        //
        // Lowercase to satisfy the wire's protovalidate ident pattern;
        // see CallerAllowedOps note in Capabilities for the
        // capability-list variant which DOES filter by AllowedOps
        // (capabilities are a hint to clients, not a strict spec).
        foreach (var op in new[] { PishockOp.Vibrate, PishockOp.Beep, PishockOp.Shock })
        {
            opDef.EnumValues.Add(op.ToString().ToLowerInvariant());
        }
        schema.Parameters.Add(opDef);

        schema.Parameters.Add(new ParameterDef
        {
            Name = "duration",
            Type = ParameterType.Duration,
            Required = true,
            Min = 0,
            // 15s is the manufacturer's UI ceiling; the per-descriptor
            // MaxDurationMs (default 1500ms) is a tighter daemon-level
            // cap enforced at trigger time.
            Max = 15,
            Description = "Per-op duration",
        });

        schema.Parameters.Add(new ParameterDef
        {
            Name = "intensity",
            Type = ParameterType.Number,
            Required = true,
            Min = 0,
            Max = 100,
            Unit = "%",
            Description = "Stimulation intensity (0..100)",
        });

        schema.Parameters.Add(new ParameterDef
        {
            Name = "delay_before",
            Type = ParameterType.Duration,
            Required = false,
            Min = 0,
            Max = 60,
            Description = "Quiet gap before this microsensation fires; for multi-pulse patterns",
        });

        return schema;
    }

    /// <summary>
    /// Builds a diagnostic microsensation for the admin body map's
    /// click-to-fire on a PiShock descriptor configured with these
    /// <paramref name="options"/>. Adapts to per-descriptor
    /// <see cref="PishockBackendOptions.AllowedOps"/>, per-op intensity
    /// caps, and <see cref="PishockBackendOptions.MaxDurationMs"/> so
    /// the diagnostic doesn't reject in
    /// <see cref="PishockTriggerValidator"/> on legitimate non-default
    /// configurations (e.g. a Beep-only descriptor, or
    /// <c>MaxIntensityVibrate</c> capped below the daemon's default 60).
    /// </summary>
    /// <remarks>
    /// Op selection prefers <c>Vibrate</c> (best haptic for zone
    /// identification), then <c>Beep</c> (audible confirmation, no
    /// haptic), then <c>Shock</c> (only when nothing else is allowed —
    /// the click-to-fire is a zone diagnostic, not a stress test).
    /// Intensity targets 60 and clamps to the per-op cap.
    /// Duration targets 300 ms and clamps to <c>MaxDurationMs</c>; on
    /// cloud transports an authored 300 ms rounds up to 1 s on the wire,
    /// and the validator's effective-duration check uses that rounded
    /// value when comparing against the cap — so a very low
    /// <c>MaxDurationMs</c> on cloud may still reject. That's a
    /// legitimately broken config and the rejection toast is the
    /// correct surface for it.
    /// </remarks>
    public static MicrosensationParameters BuildDiagnosticMicrosensation(PishockBackendOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var allowed = options.EffectiveAllowedOps;
        PishockOp op;
        if (allowed.Contains(PishockOp.Vibrate)) op = PishockOp.Vibrate;
        else if (allowed.Contains(PishockOp.Beep)) op = PishockOp.Beep;
        else op = PishockOp.Shock; // last resort — AllowedOps is required non-empty by the factory

        var cap = op switch
        {
            PishockOp.Shock => options.MaxIntensityShock,
            PishockOp.Vibrate => options.MaxIntensityVibrate,
            PishockOp.Beep => 100,
            _ => 0,
        };
        var intensity = Math.Clamp(60, 0, Math.Max(0, cap));

        var duration = TimeSpan.FromMilliseconds(
            Math.Min(300, Math.Max(0, options.MaxDurationMs)));

        return new MicrosensationParameters(new Dictionary<string, Backends.Internal.ParameterValue>
        {
            ["op"] = new Backends.Internal.ParameterValue.EnumValue(op.ToString().ToLowerInvariant()),
            ["intensity"] = new Backends.Internal.ParameterValue.Number(intensity),
            ["duration"] = new Backends.Internal.ParameterValue.Duration(duration),
        });
    }
}

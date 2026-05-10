// This file is excluded from compile on non-Windows hosts via the
// conditional <Compile Remove="OwoBackend.cs"/> ItemGroup in
// Smited.Daemon.Owo.csproj. The OWO SDK is restored only on Windows
// (the WINDOWS symbol is automatically defined by the net9.0-windows
// TFM, but on cross-platform builds the SDK package isn't present, so
// we additionally guard the file body with `#if WINDOWS`).

#if WINDOWS
using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Smited.Daemon.Backends;
using Smited.Daemon.Backends.Internal;
using Smited.V1;

namespace Smited.Daemon.Owo;

/// <summary>
/// Real OWO Skin haptic backend, backed by the official OWO C# SDK
/// (NuGet package <c>OWO</c>) and the locally-running MyOWO desktop app.
/// </summary>
/// <remarks>
/// <para>
/// Connects via the SDK's auto-discovery handshake (or with a manual IP
/// from <see cref="OwoBackendOptions.ManualIp"/>) to a paired, calibrated
/// OWO Skin reachable through the MyOWO app's local TCP service.
/// Sensations are translated from smited's domain model (zones,
/// microsensations, parameters) into the SDK's <c>SensationsFactory</c>
/// API.
/// </para>
/// <para>
/// Single-shot per OWO's own concurrency rules: only one sensation plays
/// at a time, and a new <c>Send</c> cancels the previous. The reported
/// <see cref="Concurrency"/> matches that reality
/// (<c>max_concurrent: 1</c>, <c>policy: CANCEL_OLDEST</c>) and is
/// intentionally identical to <c>MockOwoBackend</c>'s, so a sensation
/// library authored against the mock works against the real backend
/// without modification.
/// </para>
/// </remarks>
public sealed class OwoBackend : IHapticBackend
{
    private readonly OwoBackendOptions _options;
    private readonly IOwoSdk _sdk;
    private readonly TimeProvider _time;
    private readonly ILogger<OwoBackend> _logger;
    private readonly Channel<BackendEvent> _events = Channel.CreateBounded<BackendEvent>(
        new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>
    /// Constructed by the daemon's <c>BackendBootstrapper</c> via
    /// <c>ActivatorUtilities.CreateInstance</c>. All collaborators are
    /// resolved from the host DI container — <see cref="IOwoSdk"/> is
    /// registered to <c>StaticOwoSdk</c> on Windows when
    /// <c>EnableOwo</c> is true, otherwise this backend never gets
    /// constructed.
    /// </summary>
    public OwoBackend(
        OwoBackendOptions options,
        IOwoSdk sdk,
        TimeProvider time,
        ILogger<OwoBackend> logger)
    {
        _options = options;
        _sdk = sdk;
        _time = time;
        _logger = logger;

        Zones = BuildZones();
        Parameters = BuildParameters();
        Concurrency = new ConcurrencyModel
        {
            MaxConcurrent = 1,
            Policy = ConcurrencyPolicy.CancelOldest,
        };
    }

    /// <inheritdoc />
    public string Id => _options.BackendId;

    /// <inheritdoc />
    public string Kind => "owo_skin";

    /// <inheritdoc />
    public string DisplayName => "OWO Skin";

    /// <inheritdoc />
    public BackendStatus Status { get; private set; } = BackendStatus.Disconnected;

    /// <inheritdoc />
    public IReadOnlyList<string> Capabilities { get; } = new[]
    {
        "ems", "zoned", "calibrated",
    };

    /// <inheritdoc />
    public ZoneTopology Zones { get; }

    /// <inheritdoc />
    public ParameterSchema Parameters { get; }

    /// <inheritdoc />
    public ConcurrencyModel Concurrency { get; }

    /// <summary>
    /// Calibration mirror. <c>null</c> until <see cref="ConnectAsync"/>
    /// succeeds, after which it reads as <c>Calibrated = true</c> with a
    /// connect-time stamp — see the constructor remarks on
    /// <c>LastCalibratedAt</c> for why the timestamp is approximate.
    /// </summary>
    public CalibrationState? Calibration { get; private set; }

    /// <inheritdoc />
    public Struct? Extras => null;

    /// <inheritdoc />
    public IAsyncEnumerable<BackendEvent> Events => _events.Reader.ReadAllAsync();

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken ct)
    {
        Status = BackendStatus.Disconnected;

        _sdk.Configure(_options.GameDisplayName);

        try
        {
            if (!string.IsNullOrEmpty(_options.ManualIp))
            {
                _logger.LogInformation(
                    "OWO backend {Id} connecting to MyOWO at {Ip}",
                    Id, _options.ManualIp);
                await _sdk.ConnectAsync(_options.ManualIp).WaitAsync(ct).ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation(
                    "OWO backend {Id} auto-connecting to MyOWO; pick this entry in the MyOWO 'Scan Games' panel if pairing stalls",
                    Id);
                await _sdk.AutoConnectAsync().WaitAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            Status = BackendStatus.Disconnected;
            throw;
        }
        catch (Exception ex)
        {
            Status = BackendStatus.Error;
            _logger.LogError(ex,
                "OWO backend {Id} failed to connect; ensure MyOWO is running and the device is paired and calibrated",
                Id);
            throw;
        }

        if (!_sdk.IsConnected)
        {
            Status = BackendStatus.Error;
            throw new InvalidOperationException(
                "OWO SDK reports IsConnected=false after Connect/AutoConnect succeeded");
        }

        Status = BackendStatus.Ready;
        // The MyOWO app refuses to pair with an uncalibrated device, so the
        // moment AutoConnect/Connect succeeds we know calibration is present.
        // The SDK does not expose the calibration timestamp from MyOWO, so we
        // record the connect time here as a best-effort approximation.
        Calibration = new CalibrationState
        {
            Calibrated = true,
            LastCalibratedAt = Timestamp.FromDateTimeOffset(_time.GetUtcNow()),
        };

        _logger.LogInformation(
            "OWO backend {Id} connected, calibrated and ready", Id);
    }

    /// <inheritdoc />
    public Task<BackendTriggerResult> TriggerAsync(
        BackendTriggerRequest request, CancellationToken ct) =>
        throw new NotSupportedException(
            "TriggerAsync is wired in commit O3 via SensationsFactory.");

    /// <inheritdoc />
    public Task<int> StopAsync(BackendStopRequest request, CancellationToken ct) =>
        throw new NotSupportedException(
            "StopAsync is wired in commit O3 against OWO.Stop().");

    /// <inheritdoc />
    public ValueTask DisposeAsync() =>
        throw new NotSupportedException(
            "DisposeAsync is wired in commit O4 alongside the heartbeat "
            + "poller and event channel completion.");

    private static ZoneTopology BuildZones()
    {
        // Mirrors MockOwoBackend.BuildZones exactly so a sensation library
        // authored against the mock backend works on the real one without
        // re-mapping. The OWO Skin's actual electrode positions are
        // approximations in body-frame coordinates; consumers should treat
        // these as hints rather than precise spatial offsets.
        var t = new ZoneTopology();
        AddZone(t, "pectoral_l", "Left pectoral", 0.4f, 0.7f, 0.3f);
        AddZone(t, "pectoral_r", "Right pectoral", 0.6f, 0.7f, 0.3f);
        AddZone(t, "abdominal_l", "Left abdominal", 0.4f, 0.5f, 0.3f);
        AddZone(t, "abdominal_r", "Right abdominal", 0.6f, 0.5f, 0.3f);
        AddZone(t, "lumbar_l", "Left lumbar", 0.4f, 0.5f, 0.7f);
        AddZone(t, "lumbar_r", "Right lumbar", 0.6f, 0.5f, 0.7f);
        AddZone(t, "dorsal_l", "Left dorsal", 0.4f, 0.7f, 0.7f);
        AddZone(t, "dorsal_r", "Right dorsal", 0.6f, 0.7f, 0.7f);
        AddZone(t, "arm_l", "Left arm", 0.2f, 0.6f, 0.5f);
        AddZone(t, "arm_r", "Right arm", 0.8f, 0.6f, 0.5f);

        AddGroup(t, "torso", "Torso",
            "pectoral_l", "pectoral_r",
            "abdominal_l", "abdominal_r",
            "lumbar_l", "lumbar_r",
            "dorsal_l", "dorsal_r");
        AddGroup(t, "arms", "Arms", "arm_l", "arm_r");
        AddGroup(t, "all", "All zones",
            "pectoral_l", "pectoral_r",
            "abdominal_l", "abdominal_r",
            "lumbar_l", "lumbar_r",
            "dorsal_l", "dorsal_r",
            "arm_l", "arm_r");
        return t;
    }

    private static void AddZone(ZoneTopology t, string id, string display, float x, float y, float z)
    {
        t.Zones.Add(new Zone
        {
            Id = id,
            DisplayName = display,
            Position = new PositionHint { X = x, Y = y, Z = z, Frame = "body" },
        });
    }

    private static void AddGroup(ZoneTopology t, string id, string display, params string[] members)
    {
        var g = new ZoneGroup { Id = id, DisplayName = display };
        foreach (var m in members)
        {
            g.ZoneIds.Add(m);
        }
        t.Groups.Add(g);
    }

    private static ParameterSchema BuildParameters()
    {
        // Mirrors MockOwoBackend.BuildParameters exactly. The OWO SDK's
        // SensationsFactory.Create() takes the same conceptual fields
        // (frequency, intensity, duration, ramp_up, ramp_down,
        // exit_delay), so a sensation file is portable between backends.
        var s = new ParameterSchema();
        s.Parameters.Add(MakeNumber("frequency", required: true, min: 1, max: 100, unit: "Hz",
            description: "Carrier frequency"));
        s.Parameters.Add(MakeNumber("intensity", required: true, min: 0, max: 100, unit: "%",
            description: "Stimulation intensity (% of calibrated maximum)"));
        s.Parameters.Add(MakeDuration("duration", required: true, min: 0, max: 10,
            description: "Active stimulation length"));
        s.Parameters.Add(MakeDuration("ramp_up", required: false, min: 0, max: 5,
            description: "Linear ramp-up before peak"));
        s.Parameters.Add(MakeDuration("ramp_down", required: false, min: 0, max: 5,
            description: "Linear ramp-down after peak"));
        s.Parameters.Add(MakeDuration("exit_delay", required: false, min: 0, max: 5,
            description: "Quiet trailing delay"));
        return s;
    }

    private static ParameterDef MakeNumber(
        string name, bool required, double min, double max, string unit, string description) =>
        new()
        {
            Name = name,
            Type = ParameterType.Number,
            Required = required,
            Min = min,
            Max = max,
            Unit = unit,
            Description = description,
        };

    private static ParameterDef MakeDuration(
        string name, bool required, double min, double max, string description) =>
        new()
        {
            Name = name,
            Type = ParameterType.Duration,
            Required = required,
            Min = min,
            Max = max,
            Description = description,
        };
}
#endif

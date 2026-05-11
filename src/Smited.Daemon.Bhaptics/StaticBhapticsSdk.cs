// This file is excluded from compile on non-Windows hosts via the
// conditional <Compile Remove="StaticBhapticsSdk.cs"/> ItemGroup in
// Smited.Daemon.Bhaptics.csproj. The Bhaptics.Tac NuGet package is
// restored only on Windows; the WINDOWS symbol is automatically
// defined by the net9.0-windows TFM, but on cross-platform builds the
// SDK package isn't present, so we additionally guard the file body
// with `#if WINDOWS`.

#if WINDOWS
// The NuGet package is named "Bhaptics.Tac" but the assembly inside is
// Bhaptics.Tact.dll and the C# namespace is "Bhaptics.Tact" (the
// package name omits the trailing 't' that the codebase actually uses).
// This is a long-standing inconsistency in bHaptics' SDK1 packaging.
using Bhaptics.Tact;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Smited.Daemon.Backends;

namespace Smited.Daemon.Bhaptics;

/// <summary>
/// Production <see cref="IBhapticsSdk"/> wrapping
/// <c>Bhaptics.Tact.HapticPlayer</c>. Singleton in DI; every
/// per-device backend (<c>bhaptics_vest</c>, <c>bhaptics_sleeve_l</c>,
/// etc.) shares it because the Player is a per-host process that
/// owns all paired devices.
/// </summary>
public sealed class StaticBhapticsSdk : IBhapticsSdk
{
    private readonly ILogger<StaticBhapticsSdk> _logger;
    private readonly IOptions<BhapticsGlobalOptions> _globalOptions;
    private readonly object _initLock = new();
    private HapticPlayer? _player;
    private bool _initialized;
    private volatile bool _playerConnected;

    public StaticBhapticsSdk(
        IOptions<BhapticsGlobalOptions> globalOptions,
        ILogger<StaticBhapticsSdk> logger)
    {
        _globalOptions = globalOptions;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken ct)
    {
        lock (_initLock)
        {
            if (_initialized)
            {
                return Task.CompletedTask;
            }

            var appId = _globalOptions.Value.AppId;
            // HapticPlayer's ctor opens the WebSocket asynchronously and
            // (with tryReconnect: true) enters an internal reconnect loop
            // if the Player isn't running yet. That matches what smited
            // wants on startup: per-backend Status stays Disconnected
            // until the heartbeat sees IsDeviceConnected flip true, with
            // no early throw if the user hasn't launched the Player yet.
            //
            // The connection callback is the SDK's only signal for
            // "Player is reachable" — there is no IsActive() parameterless
            // overload in 1.4.2. We mirror it into _playerConnected so
            // IsPlayerRunning can answer synchronously.
            _player = new HapticPlayer(OnConnectionChanged, tryReconnect: true);
            _initialized = true;
            _logger.LogInformation(
                "bHaptics SDK initialized (smited AppId={AppId}); awaiting Player connection",
                appId);
        }
        return Task.CompletedTask;
    }

    private void OnConnectionChanged(bool connected)
    {
        _playerConnected = connected;
        _logger.LogInformation(
            "bHaptics Player connection state changed to {Connected}", connected);
    }

    /// <inheritdoc />
    public bool IsPlayerRunning => _player is not null && _playerConnected;

    // Submission keys for the two vest surfaces. Two distinct keys
    // (not just one "vest") so each surface's submission can be
    // tracked / stopped independently by the Player.
    private const string VestFrontKey = "vest_front";
    private const string VestBackKey = "vest_back";

    /// <inheritdoc />
    public bool IsDeviceConnected(string deviceKey)
    {
        if (_player is null)
        {
            return false;
        }
        if (deviceKey == "vest")
        {
            // The TactSuit vest exposes two surfaces (VestFront,
            // VestBack) AND a parent PositionType.Vest. The parent is
            // not reliable for motor-array submission per Bhaptics.Tac
            // 1.4.2 docs (the raw-motor API targets surfaces with
            // byte[20] payloads each — see Submit below), but the
            // Player can report a paired vest under any of the three
            // values depending on firmware/Player version. Treat the
            // vest as "connected" if ANY of the three reports active
            // so ConnectAsync and the heartbeat don't sit permanently
            // in Disconnected on Players that only flag the parent
            // PositionType.Vest. A transient single-channel drop
            // likewise doesn't make the daemon flap its status.
            return _player.IsActive(PositionType.Vest)
                || _player.IsActive(PositionType.VestFront)
                || _player.IsActive(PositionType.VestBack);
        }
        var position = MapDeviceKey(deviceKey);
        return _player.IsActive(position);
    }

    /// <inheritdoc />
    public void Submit(string deviceKey, byte[] motorIntensities, int durationMs)
    {
        if (_player is null)
        {
            throw new InvalidOperationException(
                "StaticBhapticsSdk not initialized; call InitializeAsync first");
        }

        if (deviceKey == "vest")
        {
            // Split the 40-byte payload across the two vest surfaces:
            // bytes 0..19 go to VestFront, 20..39 go to VestBack. The
            // raw-motor APIs in Bhaptics.Tac 1.4.2 take per-surface
            // byte[20] payloads and do not support a 40-byte
            // PositionType.Vest submission for motor-array
            // ([README at https://www.nuget.org/packages/Bhaptics.Tac/1.4.2]),
            // so a single Submit against PositionType.Vest silently
            // fails to drive back-zone sensations (e.g. deploy_success
            // hitting dorsal_l/r).
            //
            // BhapticsBackendBase always passes a 40-byte payload for
            // the vest (MotorCount=40), so this length assertion holds
            // by construction; the explicit guard prevents a future
            // caller from feeding the SDK a wrong-length array.
            if (motorIntensities.Length != 40)
            {
                throw new ArgumentException(
                    $"Vest payload must be exactly 40 bytes, got {motorIntensities.Length}",
                    nameof(motorIntensities));
            }
            var front = new byte[20];
            var back = new byte[20];
            Buffer.BlockCopy(motorIntensities, 0, front, 0, 20);
            Buffer.BlockCopy(motorIntensities, 20, back, 0, 20);
            _player.Submit(VestFrontKey, PositionType.VestFront, front, durationMs);
            _player.Submit(VestBackKey, PositionType.VestBack, back, durationMs);
            return;
        }

        var position = MapDeviceKey(deviceKey);
        // HapticPlayer.Submit(string key, PositionType position, byte[] motorBytes, int durationMillis).
        // The "key" is a per-submission identifier the Player tracks for
        // later StopDevice/TurnOff calls; for non-vest devices we use
        // the smited-side deviceKey so StopDevice can target it
        // without bookkeeping.
        _player.Submit(deviceKey, position, motorIntensities, durationMs);
    }

    /// <inheritdoc />
    public void StopDevice(string deviceKey)
    {
        if (_player is null) return;
        if (deviceKey == "vest")
        {
            // Mirror the Submit-time split: stop both surface keys so
            // a vest StopAsync silences front AND back actuators.
            _player.TurnOff(VestFrontKey);
            _player.TurnOff(VestBackKey);
            return;
        }
        _player.TurnOff(deviceKey);
    }

    /// <inheritdoc />
    public void StopAll()
    {
        _player?.TurnOff();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // HapticPlayer is IDisposable, not IAsyncDisposable. Wrap the
        // synchronous dispose in an async-shaped DisposeAsync for
        // IBhapticsSdk interface symmetry with the rest of the daemon's
        // DI surface (IHapticBackend is IAsyncDisposable etc.).
        _player?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Translate smited's device-key vocabulary into the SDK's
    /// <see cref="PositionType"/> enum. The vest key intentionally
    /// has no single mapping — vest submissions route through the
    /// VestFront/VestBack split inside <see cref="Submit"/> and
    /// <see cref="IsDeviceConnected"/>. Any string not in this set
    /// indicates a caller bug (the backend's <c>DeviceKey</c> is fixed
    /// per concrete class).
    /// </summary>
    private static PositionType MapDeviceKey(string deviceKey) => deviceKey switch
    {
        "sleeve_l" => PositionType.ForearmL,
        "sleeve_r" => PositionType.ForearmR,
        "feet_l" => PositionType.FootL,
        "feet_r" => PositionType.FootR,
        _ => throw new ArgumentException(
            $"Unknown bHaptics device key '{deviceKey}'", nameof(deviceKey)),
    };
}
#endif

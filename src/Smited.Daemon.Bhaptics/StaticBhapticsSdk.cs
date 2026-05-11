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

    /// <inheritdoc />
    public bool IsDeviceConnected(string deviceKey)
    {
        if (_player is null)
        {
            return false;
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
        var position = MapDeviceKey(deviceKey);
        // HapticPlayer.Submit(string key, PositionType position, byte[] motorBytes, int durationMillis).
        // The "key" is a per-submission identifier the Player tracks for
        // later StopDevice/TurnOff calls; we use the smited-side
        // deviceKey so StopDevice can target it without bookkeeping.
        _player.Submit(deviceKey, position, motorIntensities, durationMs);
    }

    /// <inheritdoc />
    public void StopDevice(string deviceKey)
    {
        _player?.TurnOff(deviceKey);
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
    /// <see cref="PositionType"/> enum. Any string not in this set
    /// indicates a caller bug (the backend's <c>DeviceKey</c> is fixed
    /// per concrete class).
    /// </summary>
    private static PositionType MapDeviceKey(string deviceKey) => deviceKey switch
    {
        "vest" => PositionType.Vest,
        "sleeve_l" => PositionType.ForearmL,
        "sleeve_r" => PositionType.ForearmR,
        "feet_l" => PositionType.FootL,
        "feet_r" => PositionType.FootR,
        _ => throw new ArgumentException(
            $"Unknown bHaptics device key '{deviceKey}'", nameof(deviceKey)),
    };
}
#endif

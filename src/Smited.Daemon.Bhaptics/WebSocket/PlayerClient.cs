using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Smited.Daemon.Bhaptics.WebSocket;

/// <summary>
/// Minimal WebSocket client to a local bHaptics Player instance.
/// Manages the connection lifecycle, the outbound submit pipeline, and
/// the inbound read loop. Surfaces device-status updates and
/// peer-initiated disconnects through events.
///
/// Internal because nothing outside <c>Smited.Daemon.Bhaptics</c>
/// should reference the wire-format types directly — the daemon talks
/// to the backend only through <c>IHapticBackend</c>.
/// </summary>
internal sealed class PlayerClient : IAsyncDisposable
{
    private readonly Uri _endpoint;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _readLoopCts;
    private Task? _readLoop;
    private int _disconnectedFired;

    /// <summary>
    /// Construct a client. Takes a non-generic <see cref="ILogger"/> so
    /// callers can pass any typed logger (e.g. their own
    /// <c>ILogger&lt;BhapticsBackend&gt;</c>) without a separate
    /// <c>ILoggerFactory</c> hop.
    /// </summary>
    public PlayerClient(Uri endpoint, ILogger log)
    {
        _endpoint = endpoint;
        _log = log;
    }

    /// <summary>Fires once per parsed inbound <c>deviceStatus</c> frame.</summary>
    public event Action<IReadOnlyList<DeviceStatus>>? DeviceStatusChanged;

    /// <summary>
    /// Fires exactly once when the Player closes the socket or the
    /// read loop terminates with an error. Argument is the underlying
    /// exception, or <c>null</c> on a clean close.
    /// </summary>
    public event Action<Exception?>? Disconnected;

    /// <summary>
    /// Open the WebSocket connection and start the read loop. Returns
    /// when the handshake completes; throws an
    /// <see cref="InvalidOperationException"/> with the endpoint embedded
    /// in the message if the Player isn't reachable.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(_endpoint, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ws.Dispose();
            throw new InvalidOperationException(
                $"Failed to connect to bHaptics Player at {_endpoint.Authority}: {ex.Message}. Is bHaptics Player running?",
                ex);
        }

        _ws = ws;
        _readLoopCts = new CancellationTokenSource();
        _readLoop = Task.Run(() => ReadLoopAsync(_readLoopCts.Token));
        _log.LogInformation("Connected to bHaptics Player at {Endpoint}", _endpoint);
    }

    /// <summary>
    /// Submit a programmatic dot-mode pattern. Returns the assigned
    /// pattern key (a GUID) so a later <see cref="TryCancelAsync"/>
    /// can target it. Throws if the connection is closed.
    /// </summary>
    public async Task<string> SubmitDotPatternAsync(
        Position position,
        IReadOnlyList<DotPoint> dots,
        TimeSpan duration,
        CancellationToken ct)
    {
        var ws = _ws ?? throw new InvalidOperationException("PlayerClient is not connected.");
        var key = Guid.NewGuid().ToString("N");
        var durationMs = (int)Math.Max(0, duration.TotalMilliseconds);

        var frame = new SubmitFrame
        {
            Type = "frame",
            Submit = new[]
            {
                new SubmitEntry
                {
                    Type = "dotMode",
                    Key = key,
                    DurationMillis = durationMs,
                    Frame = new Frame
                    {
                        Position = position,
                        DotPoints = dots,
                        DurationMillis = durationMs,
                    },
                },
            },
        };

        await SendJsonAsync(ws, frame, ct).ConfigureAwait(false);
        return key;
    }

    /// <summary>
    /// Cancel an in-flight pattern by key. Best-effort fire-and-forget;
    /// the Player echoes no response. Sends an empty dot-mode frame
    /// against the same key, which the Player treats as a stop.
    /// </summary>
    public async Task TryCancelAsync(string patternKey, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) return;

        var frame = new SubmitFrame
        {
            Type = "frame",
            Submit = new[]
            {
                new SubmitEntry
                {
                    Type = "dotMode",
                    Key = patternKey,
                    DurationMillis = 0,
                    Frame = new Frame
                    {
                        Position = Position.Vest,
                        DotPoints = Array.Empty<DotPoint>(),
                        DurationMillis = 0,
                    },
                },
            },
        };

        try
        {
            await SendJsonAsync(ws, frame, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "TryCancelAsync({Key}) failed silently", patternKey);
        }
    }

    /// <summary>Cancel every active pattern across every position.</summary>
    public async Task CancelAllAsync(CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) return;

        var frame = new SubmitFrame
        {
            Type = "frame",
            Submit = Array.Empty<SubmitEntry>(),
        };

        try
        {
            await SendJsonAsync(ws, frame, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "CancelAllAsync failed silently");
        }
    }

    private async Task SendJsonAsync(ClientWebSocket ws, object payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var ws = _ws!;
        var buffer = new byte[16 * 1024];
        Exception? terminal = null;
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        try
                        {
                            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack-close", ct).ConfigureAwait(false);
                        }
                        catch (Exception) { /* peer already gone */ }
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                ms.Position = 0;
                using var doc = JsonDocument.Parse(ms);
                DispatchInbound(doc.RootElement);
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (Exception ex)
        {
            terminal = ex;
            _log.LogWarning(ex, "bHaptics Player read loop terminated unexpectedly");
        }
        finally
        {
            FireDisconnectedOnce(terminal);
        }
    }

    private void DispatchInbound(JsonElement root)
    {
        if (!root.TryGetProperty("type", out var typeProp)) return;
        var type = typeProp.GetString();
        if (!string.Equals(type, "deviceStatus", StringComparison.Ordinal)) return;

        if (!root.TryGetProperty("devices", out var devicesProp) ||
            devicesProp.ValueKind != JsonValueKind.Array)
        {
            DeviceStatusChanged?.Invoke(Array.Empty<DeviceStatus>());
            return;
        }

        var devices = devicesProp.Deserialize<List<DeviceStatus>>() ?? new List<DeviceStatus>();
        DeviceStatusChanged?.Invoke(devices);
    }

    private void FireDisconnectedOnce(Exception? terminal)
    {
        if (Interlocked.Exchange(ref _disconnectedFired, 1) == 0)
        {
            Disconnected?.Invoke(terminal);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _readLoopCts?.Cancel();
        var ws = _ws;
        if (ws is not null)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "client-shutdown", CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception) { /* tear-down best-effort */ }
            ws.Dispose();
        }

        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.ConfigureAwait(false);
            }
            catch (Exception) { /* tear-down best-effort */ }
        }

        _readLoopCts?.Dispose();
        _sendLock.Dispose();
    }
}

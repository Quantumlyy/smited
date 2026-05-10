using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Smited.Daemon.Bhaptics.WebSocket;

namespace Smited.Daemon.Tests.Bhaptics.Fixtures;

/// <summary>
/// In-process Kestrel WebSocket fixture that emulates a bHaptics Player
/// instance for <c>PlayerClient</c> tests. Binds <c>/v2/feedbacks</c>
/// on an OS-assigned port, captures every inbound frame as a parsed
/// <see cref="JsonElement"/>, and exposes a method to push outbound
/// frames the way the real Player would. Lives only for the test that
/// owns it; auto-disposes the underlying app on <see cref="DisposeAsync"/>.
/// </summary>
internal sealed class BhapticsPlayerSimulator : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly Channel<JsonElement> _received;
    private readonly TaskCompletionSource<WebSocket> _connectedSocket;
    private readonly CancellationTokenSource _shutdown;

    private BhapticsPlayerSimulator(
        WebApplication app,
        Uri endpoint,
        Channel<JsonElement> received,
        TaskCompletionSource<WebSocket> connectedSocket,
        CancellationTokenSource shutdown)
    {
        _app = app;
        Endpoint = endpoint;
        _received = received;
        _connectedSocket = connectedSocket;
        _shutdown = shutdown;
    }

    /// <summary>The <c>ws://...</c> URI a <c>PlayerClient</c> should connect to.</summary>
    public Uri Endpoint { get; }

    /// <summary>Reader for inbound frames the simulator captured.</summary>
    public ChannelReader<JsonElement> ReceivedFrames => _received.Reader;

    /// <summary>
    /// Boot a simulator on an auto-assigned port. Returns once the
    /// server is listening; the connect handshake hasn't happened yet.
    /// When <paramref name="initialDeviceStatus"/> is non-null, the
    /// simulator sends a <c>deviceStatus</c> frame with that payload
    /// immediately after accepting each WebSocket — this mirrors what
    /// the real bHaptics Player does on connect, and lets tests verify
    /// the backend's <c>InitialStatusTimeoutMillis</c> wait reaches the
    /// expanded topology before <c>ConnectAsync</c> returns.
    /// </summary>
    public static async Task<BhapticsPlayerSimulator> StartAsync(
        IReadOnlyList<DeviceStatus>? initialDeviceStatus = null)
    {
        var received = Channel.CreateUnbounded<JsonElement>();
        var connectedSocket = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdown = new CancellationTokenSource();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseWebSockets();

        app.Map("/v2/feedbacks", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var ws = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            connectedSocket.TrySetResult(ws);

            if (initialDeviceStatus is { Count: > 0 })
            {
                var frame = new
                {
                    type = "deviceStatus",
                    devices = initialDeviceStatus.Select(d => new
                    {
                        position = (int)d.Position,
                        connected = d.Connected,
                        batteryPercent = d.BatteryPercent,
                    }).ToArray(),
                };
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(frame));
                await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, shutdown.Token).ConfigureAwait(false);
            }

            await ReadLoopAsync(ws, received.Writer, shutdown.Token).ConfigureAwait(false);
        });

        await app.StartAsync().ConfigureAwait(false);

        var server = app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()!.Addresses;
        var http = new Uri(addresses.First());
        var endpoint = new UriBuilder("ws", http.Host, http.Port, "/v2/feedbacks").Uri;

        return new BhapticsPlayerSimulator(app, endpoint, received, connectedSocket, shutdown);
    }

    /// <summary>
    /// Push a JSON-serialised inbound frame to the connected client.
    /// Throws if the client hasn't connected within the supplied timeout.
    /// </summary>
    public async Task PushAsync(object frame, TimeSpan? connectTimeout = null, CancellationToken ct = default)
    {
        var ws = await WaitForConnectionAsync(connectTimeout ?? TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(frame);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Close the active socket from the simulator side. The client
    /// observes this as a peer-initiated close. Best-effort: tests
    /// only care that the connection ends, so a racy close-handshake
    /// failure is swallowed (the peer may have closed first or the
    /// Kestrel request stream may be disposed mid-handshake during
    /// concurrent backend tear-down).
    /// </summary>
    public async Task CloseAsync(CancellationToken ct = default)
    {
        var ws = await WaitForConnectionAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        try
        {
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "simulator-close", ct).ConfigureAwait(false);
        }
        catch (WebSocketException) { /* peer already closed or handshake raced */ }
        catch (ObjectDisposedException) { /* underlying stream torn down */ }
        catch (System.IO.IOException) { /* request body stream gone */ }
    }

    private async Task<WebSocket> WaitForConnectionAsync(TimeSpan timeout, CancellationToken ct)
    {
        var winner = await Task.WhenAny(_connectedSocket.Task, Task.Delay(timeout, ct)).ConfigureAwait(false);
        if (winner != _connectedSocket.Task)
        {
            throw new TimeoutException("Player simulator: no client connected within the deadline.");
        }
        return await _connectedSocket.Task.ConfigureAwait(false);
    }

    private static async Task ReadLoopAsync(WebSocket ws, ChannelWriter<JsonElement> writer, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
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
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "ack-close", ct).ConfigureAwait(false);
                        }
                        catch (WebSocketException) { /* peer already closed */ }
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                ms.Position = 0;
                using var doc = JsonDocument.Parse(ms);
                writer.TryWrite(doc.RootElement.Clone());
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (WebSocketException) { /* peer dropped */ }
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try { _shutdown.Cancel(); } catch (ObjectDisposedException) { }
        _received.Writer.TryComplete();
        try
        {
            await _app.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception) { /* tear-down best-effort */ }
        try { _shutdown.Dispose(); } catch (ObjectDisposedException) { }
    }
}

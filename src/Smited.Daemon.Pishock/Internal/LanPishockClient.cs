using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Smited.Daemon.Pishock.Internal;

/// <summary>
/// PiShock LAN-mode transport: HTTP POST directly to the device on the
/// local network. No auth (the device trusts whoever can reach it).
/// Duration on the wire is in milliseconds, which is the headline
/// advantage over the cloud transport's 1-second minimum.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint path (<c>/api/1/operate</c>) and JSON body shape come
/// from the openshock-compatible LAN firmware contract. The original
/// PiShock LAN firmware's exact shape is proprietary and may differ;
/// before deploying with a real device, capture one request from the
/// PiShock mobile app on the same LAN (tcpdump or a transparent proxy)
/// and confirm this client matches. The smoke-test runbook in
/// <c>docs/pishock-smoke.md</c> walks through that capture step.
/// </para>
/// </remarks>
internal sealed class LanPishockClient : IPishockClient
{
    private static readonly JsonSerializerOptions BodyJsonOptions = new()
    {
        PropertyNamingPolicy = null,
    };

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly PishockBackendOptions _options;
    private readonly ILogger<LanPishockClient> _logger;

    public LanPishockClient(
        HttpClient http,
        PishockBackendOptions options,
        string descriptorId,
        ILogger<LanPishockClient> logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(descriptorId);
        ArgumentNullException.ThrowIfNull(logger);

        _http = http;
        _options = options;
        _logger = logger;

        var port = options.DevicePort ?? 80;
        var uri = new UriBuilder("http", options.DeviceIp ?? "", port, "/api/1/operate");
        _endpoint = uri.Uri;
    }

    public async Task<PishockOpResult> SendOpAsync(
        PishockOp op, int durationMs, int intensity, CancellationToken ct)
    {
        var body = new LanOperateBody
        {
            op = (int)op,
            duration = durationMs,
            intensity = intensity,
        };

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(_options.RequestTimeoutMs));

            using var response = await _http.PostAsJsonAsync(_endpoint, body, BodyJsonOptions, cts.Token)
                .ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return new PishockOpResult(true, text, null);
            }

            _logger.LogWarning(
                "PiShock LAN device at {Endpoint} rejected {Op} for {DurationMs}ms at {Intensity}%: HTTP {Status} {Body}",
                _endpoint, op, durationMs, intensity, (int)response.StatusCode, text);
            return new PishockOpResult(false, text,
                $"HTTP {(int)response.StatusCode}: {text}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new PishockOpResult(false, null, "timeout");
        }
        catch (HttpRequestException ex)
        {
            return new PishockOpResult(false, null, $"HTTP error: {ex.Message}");
        }
    }

    /// <summary>
    /// Lowercase property names match the openshock-compatible firmware's
    /// documented shape. If the original PiShock LAN firmware uses
    /// PascalCase, this is the field to flip after the captured-trace
    /// verification step.
    /// </summary>
#pragma warning disable IDE1006 // Naming — wire-required casing
    private sealed class LanOperateBody
    {
        public int op { get; set; }
        public int duration { get; set; }
        public int intensity { get; set; }
    }
#pragma warning restore IDE1006
}

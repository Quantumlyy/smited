using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Smited.Daemon.Pishock.Internal;

/// <summary>
/// PiShock cloud-mode transport: HTTPS POST to
/// <c>do.pishock.com/api/apioperate</c> with username, API key, and a
/// per-shocker share code. Duration on the wire is in <em>whole
/// seconds</em>; sub-second intent rounds up.
/// </summary>
/// <remarks>
/// <para>
/// The cloud API returns plain text rather than HTTP status codes for
/// domain failures: a 200 with body <c>"Operation Succeeded."</c> means
/// the device fired; any other body (e.g. <c>"Not Authorized"</c>,
/// <c>"This shocker is offline."</c>) means the device did not. The
/// client treats any non-success body as a failure and surfaces the
/// raw text in <see cref="PishockOpResult.ErrorMessage"/>.
/// </para>
/// <para>
/// Use LAN mode if you need sub-second duration control or want to
/// avoid the cloud round-trip latency. The cloud transport is fine for
/// "fire a 1s vibrate when X happens" patterns; less so for fast
/// staccato sequences.
/// </para>
/// </remarks>
internal sealed class CloudPishockClient : IPishockClient
{
    private static readonly Uri Endpoint = new("https://do.pishock.com/api/apioperate");

    /// <summary>
    /// PiShock's cloud API expects exact-case field names
    /// (<c>Username</c>, <c>Apikey</c>, <c>Code</c>, ...). System.Text.Json's
    /// <c>JsonSerializerDefaults.Web</c> default would camelCase them and
    /// the API would reject every request as malformed; an explicit
    /// PascalCase serializer keeps the field names as authored.
    /// </summary>
    private static readonly JsonSerializerOptions BodyJsonOptions = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly HttpClient _http;
    private readonly PishockBackendOptions _options;
    private readonly string _name;
    private readonly ILogger<CloudPishockClient> _logger;

    public CloudPishockClient(
        HttpClient http,
        PishockBackendOptions options,
        string descriptorId,
        ILogger<CloudPishockClient> logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(descriptorId);
        ArgumentNullException.ThrowIfNull(logger);

        _http = http;
        _options = options;
        // The Name field surfaces in the PiShock account UI as the
        // attribution for the op. "smited-{id}" makes it obvious which
        // descriptor fired which op when reviewing the cloud history.
        _name = $"smited-{descriptorId}";
        _logger = logger;
    }

    public async Task<PishockOpResult> SendOpAsync(
        PishockOp op, int durationMs, int intensity, CancellationToken ct)
    {
        // Cloud API takes Duration in whole seconds, range 1..15.
        // Round UP from milliseconds — undershoot would silently fire a
        // shorter op than the user authored, which is more surprising
        // than firing a slightly longer one. The 200ms vibrate becomes
        // 1s; the LAN transport is the answer for sub-second control.
        var durationSeconds = Math.Max(1, (int)Math.Ceiling(durationMs / 1000.0));

        var body = new CloudOperateBody
        {
            Username = _options.Username ?? "",
            Apikey = _options.ApiKey ?? "",
            Code = _options.ShareCode ?? "",
            Name = _name,
            Op = (int)op,
            Duration = durationSeconds,
            Intensity = intensity,
        };

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(_options.RequestTimeoutMs));

            using var response = await _http.PostAsJsonAsync(Endpoint, body, BodyJsonOptions, cts.Token)
                .ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            var ok = response.IsSuccessStatusCode
                  && text.Contains("Operation Succeeded", StringComparison.OrdinalIgnoreCase);
            if (!ok)
            {
                _logger.LogWarning(
                    "PiShock cloud rejected {Op} for {DurationMs}ms at {Intensity}%: {Body}",
                    op, durationMs, intensity, text);
            }
            return new PishockOpResult(ok, text, ok ? null : text);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout (per-call CTS canceled) rather than caller-driven
            // cancellation. The callsite owns the original ct and would
            // see it canceled — distinguishing makes the failure
            // message accurate for log triage.
            return new PishockOpResult(false, null, "timeout");
        }
        catch (HttpRequestException ex)
        {
            return new PishockOpResult(false, null, $"HTTP error: {ex.Message}");
        }
    }

    /// <summary>
    /// JSON body shape PiShock's API expects. Field names are exact
    /// (case-sensitive) to match the documented contract.
    /// </summary>
    private sealed class CloudOperateBody
    {
        public string Username { get; set; } = "";
        public string Apikey { get; set; } = "";
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public int Op { get; set; }
        public int Duration { get; set; }
        public int Intensity { get; set; }
    }
}

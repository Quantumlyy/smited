using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Smited.Daemon.Pishock;
using Smited.Daemon.Pishock.Internal;
using Xunit;

namespace Smited.Daemon.Tests.Backends.Pishock;

/// <summary>
/// LAN client URL/body shape is the openshock-compatible firmware's
/// documented contract. Verify against a real device capture before
/// shipping to a user — see commit 4's smoke-test scaffolding.
/// </summary>
public class LanPishockClientTests
{
    [Fact]
    public async Task SendOpAsync_posts_to_device_ip_with_default_port_80()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "");
        var options = new PishockBackendOptions
        {
            Mode = PishockTransportMode.Lan,
            DeviceIp = "192.168.1.50",
            DevicePort = null,
            RequestTimeoutMs = 5000,
        };
        var client = new LanPishockClient(new HttpClient(handler), options, "left-thigh",
            NullLogger<LanPishockClient>.Instance);

        await client.SendOpAsync(PishockOp.Vibrate, 200, 30, CancellationToken.None);

        handler.LastRequest!.RequestUri!.Host.Should().Be("192.168.1.50");
        handler.LastRequest!.RequestUri!.Port.Should().Be(80);
        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/api/1/operate");
        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
    }

    [Fact]
    public async Task SendOpAsync_uses_configured_port_when_set()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "");
        var options = new PishockBackendOptions
        {
            Mode = PishockTransportMode.Lan,
            DeviceIp = "10.0.0.42",
            DevicePort = 8080,
        };
        var client = new LanPishockClient(new HttpClient(handler), options, "right-calf",
            NullLogger<LanPishockClient>.Instance);

        await client.SendOpAsync(PishockOp.Vibrate, 200, 30, CancellationToken.None);

        handler.LastRequest!.RequestUri!.Port.Should().Be(8080);
    }

    [Fact]
    public async Task SendOpAsync_sends_op_duration_intensity_in_json_body_with_ms_duration()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "");
        var options = new PishockBackendOptions
        {
            Mode = PishockTransportMode.Lan,
            DeviceIp = "192.168.1.50",
        };
        var client = new LanPishockClient(new HttpClient(handler), options, "left-thigh",
            NullLogger<LanPishockClient>.Instance);

        await client.SendOpAsync(PishockOp.Vibrate, durationMs: 250, intensity: 40, CancellationToken.None);

        var json = JsonDocument.Parse(handler.LastRequestBody!);
        json.RootElement.GetProperty("op").GetInt32().Should().Be((int)PishockOp.Vibrate);
        // LAN transport keeps milliseconds — the headline advantage over
        // cloud's 1s minimum.
        json.RootElement.GetProperty("duration").GetInt32().Should().Be(250);
        json.RootElement.GetProperty("intensity").GetInt32().Should().Be(40);
    }

    [Fact]
    public async Task SendOpAsync_treats_HTTP_200_as_accepted()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
        var options = NewLanOptions();
        var client = new LanPishockClient(new HttpClient(handler), options, "left-thigh",
            NullLogger<LanPishockClient>.Instance);

        var result = await client.SendOpAsync(PishockOp.Vibrate, 200, 30, CancellationToken.None);

        result.Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task SendOpAsync_treats_HTTP_4xx_as_failure()
    {
        var handler = new RecordingHandler(HttpStatusCode.BadRequest, "{\"error\":\"bad request\"}");
        var options = NewLanOptions();
        var client = new LanPishockClient(new HttpClient(handler), options, "left-thigh",
            NullLogger<LanPishockClient>.Instance);

        var result = await client.SendOpAsync(PishockOp.Vibrate, 200, 30, CancellationToken.None);

        result.Accepted.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    private static PishockBackendOptions NewLanOptions() => new()
    {
        Mode = PishockTransportMode.Lan,
        DeviceIp = "192.168.1.50",
        RequestTimeoutMs = 5000,
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastRequestBody { get; private set; }

        public RecordingHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body),
            };
        }
    }
}

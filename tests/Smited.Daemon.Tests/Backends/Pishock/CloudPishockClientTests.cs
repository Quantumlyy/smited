using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Smited.Daemon.Pishock;
using Smited.Daemon.Pishock.Internal;
using Xunit;

namespace Smited.Daemon.Tests.Backends.Pishock;

public class CloudPishockClientTests
{
    [Fact]
    public async Task SendOpAsync_posts_to_pishock_apioperate_endpoint()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "Operation Succeeded.");
        var client = NewClient(handler);

        await client.SendOpAsync(PishockOp.Vibrate, durationMs: 200, intensity: 30, CancellationToken.None);

        handler.LastRequest!.RequestUri.Should().Be(new Uri("https://do.pishock.com/api/apioperate"));
        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
    }

    [Fact]
    public async Task SendOpAsync_sends_documented_JSON_body_with_credentials_and_op_fields()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "Operation Succeeded.");
        var client = NewClient(handler);

        await client.SendOpAsync(PishockOp.Vibrate, durationMs: 250, intensity: 40, CancellationToken.None);

        var json = JsonDocument.Parse(handler.LastRequestBody!);
        json.RootElement.GetProperty("Username").GetString().Should().Be("test-user");
        json.RootElement.GetProperty("Apikey").GetString().Should().Be("test-key");
        json.RootElement.GetProperty("Code").GetString().Should().Be("ABCD1234");
        json.RootElement.GetProperty("Op").GetInt32().Should().Be((int)PishockOp.Vibrate);
        // Cloud API takes duration in WHOLE SECONDS (1..15). 250ms rounds
        // up to 1s — losing sub-second resolution is the cloud transport's
        // documented limitation.
        json.RootElement.GetProperty("Duration").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("Intensity").GetInt32().Should().Be(40);
        json.RootElement.GetProperty("Name").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SendOpAsync_rounds_sub_second_durations_up_to_one_second()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "Operation Succeeded.");
        var client = NewClient(handler);

        await client.SendOpAsync(PishockOp.Vibrate, durationMs: 50, intensity: 30, CancellationToken.None);

        var json = JsonDocument.Parse(handler.LastRequestBody!);
        json.RootElement.GetProperty("Duration").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task SendOpAsync_rounds_multi_second_durations_up()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "Operation Succeeded.");
        var client = NewClient(handler);

        await client.SendOpAsync(PishockOp.Vibrate, durationMs: 2200, intensity: 30, CancellationToken.None);

        var json = JsonDocument.Parse(handler.LastRequestBody!);
        // 2.2s rounds up to 3s rather than down to 2s — undershoot would
        // play less than the user authored.
        json.RootElement.GetProperty("Duration").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task SendOpAsync_treats_Operation_Succeeded_response_as_accepted()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "Operation Succeeded.");
        var client = NewClient(handler);

        var result = await client.SendOpAsync(PishockOp.Vibrate, 200, 30, CancellationToken.None);

        result.Accepted.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        result.RawResponse.Should().Be("Operation Succeeded.");
    }

    [Fact]
    public async Task SendOpAsync_treats_unexpected_body_as_failure_with_body_as_error_message()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "Not Authorized.");
        var client = NewClient(handler);

        var result = await client.SendOpAsync(PishockOp.Vibrate, 200, 30, CancellationToken.None);

        result.Accepted.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not Authorized");
    }

    [Fact]
    public async Task SendOpAsync_treats_HTTP_error_status_as_failure()
    {
        var handler = new RecordingHandler(HttpStatusCode.InternalServerError, "Server error");
        var client = NewClient(handler);

        var result = await client.SendOpAsync(PishockOp.Vibrate, 200, 30, CancellationToken.None);

        result.Accepted.Should().BeFalse();
    }

    [Fact]
    public async Task SendOpAsync_returns_failure_on_request_timeout_without_throwing()
    {
        // Hand-rolled handler that simulates a slow response (longer
        // than the configured RequestTimeoutMs). The client should bail
        // with a clean failure result rather than letting an exception
        // escape — the backend translates result.Accepted=false into a
        // BACKEND_UNAVAILABLE rejection.
        var handler = new SlowHandler(TimeSpan.FromSeconds(5));
        var options = new PishockBackendOptions
        {
            Mode = PishockTransportMode.Cloud,
            Username = "test-user",
            ApiKey = "test-key",
            ShareCode = "ABCD1234",
            RequestTimeoutMs = 50,
        };
        var http = new HttpClient(handler);
        var client = new CloudPishockClient(http, options, "left-thigh",
            NullLogger<CloudPishockClient>.Instance);

        var result = await client.SendOpAsync(PishockOp.Vibrate, 200, 30, CancellationToken.None);

        result.Accepted.Should().BeFalse();
        result.ErrorMessage.Should().Contain("timeout", because:
            "the client should classify a request-level cancellation that wasn't from the caller "
            + "as a timeout and surface it that way for log triage");
    }

    private static CloudPishockClient NewClient(HttpMessageHandler handler)
    {
        var options = new PishockBackendOptions
        {
            Mode = PishockTransportMode.Cloud,
            Username = "test-user",
            ApiKey = "test-key",
            ShareCode = "ABCD1234",
            RequestTimeoutMs = 5000,
        };
        var http = new HttpClient(handler);
        return new CloudPishockClient(http, options, "left-thigh",
            NullLogger<CloudPishockClient>.Instance);
    }

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

    private sealed class SlowHandler : HttpMessageHandler
    {
        private readonly TimeSpan _delay;

        public SlowHandler(TimeSpan delay) { _delay = delay; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(_delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

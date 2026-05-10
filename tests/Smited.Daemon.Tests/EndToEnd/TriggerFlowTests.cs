using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Smited.Daemon.Tests.Fixtures;
using Smited.V1;
using Xunit;

namespace Smited.Daemon.Tests.EndToEnd;

public class TriggerFlowTests : IDisposable
{
    private readonly DaemonFixture _fixture;

    public TriggerFlowTests()
    {
        _fixture = new DaemonFixture(seed: root =>
            SampleSensations.WriteOwo(root, "compile_error_mild.json", SampleSensations.CompileErrorMild));
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Named_sensation_succeeds_and_echoes_client_trace_id()
    {
        var response = await _fixture.Client.TriggerAsync(new TriggerRequest
        {
            BackendId = "mock-owo",
            SensationName = "compile_error_mild",
            ClientTraceId = "trace-abc123",
        });

        response.Accepted.Should().BeTrue();
        response.SensationId.Should().NotBeNullOrEmpty();
        response.ClientTraceId.Should().Be("trace-abc123");
    }

    [Fact]
    public async Task Unknown_sensation_returns_SENSATION_NOT_FOUND_with_trace_id()
    {
        var response = await _fixture.Client.TriggerAsync(new TriggerRequest
        {
            BackendId = "mock-owo",
            SensationName = "nonexistent_name",
            ClientTraceId = "trace-bad",
        });

        response.Accepted.Should().BeFalse();
        response.Error.Code.Should().Be(TriggerErrorCode.SensationNotFound);
        response.ClientTraceId.Should().Be("trace-bad");
    }

    [Fact]
    public async Task Invalid_zone_returns_INVALID_ZONE_with_field()
    {
        var request = new TriggerRequest
        {
            BackendId = "mock-owo",
            SensationName = "compile_error_mild",
            ClientTraceId = "trace-zone",
        };
        request.ZoneIds.Add("nonexistent_zone");

        var response = await _fixture.Client.TriggerAsync(request);

        response.Accepted.Should().BeFalse();
        response.Error.Code.Should().Be(TriggerErrorCode.InvalidZone);
        response.Error.Field.Should().Be("zone_ids");
    }

    [Fact]
    public async Task Wrong_parameter_type_in_inline_returns_INVALID_PARAMETER()
    {
        var inline = new InlineSensation();
        var micro = new Microsensation();
        // Schema declares 'intensity' as NUMBER; sending a duration is type mismatch.
        micro.Parameters["frequency"] = new ParameterValue { Number = 50 };
        micro.Parameters["intensity"] = new ParameterValue { Duration = Duration.FromTimeSpan(TimeSpan.FromMilliseconds(100)) };
        micro.Parameters["duration"] = new ParameterValue { Duration = Duration.FromTimeSpan(TimeSpan.FromMilliseconds(100)) };
        inline.Microsensations.Add(micro);

        var request = new TriggerRequest
        {
            BackendId = "mock-owo",
            Inline = inline,
            ClientTraceId = "trace-param",
        };
        request.ZoneIds.Add("pectoral_l");

        var response = await _fixture.Client.TriggerAsync(request);

        response.Accepted.Should().BeFalse();
        response.Error.Code.Should().Be(TriggerErrorCode.InvalidParameter);
        response.Error.Field.Should().Be("microsensations[0].parameters.intensity");
    }

    [Fact]
    public async Task Empty_backend_id_returns_BACKEND_NOT_FOUND()
    {
        var response = await _fixture.Client.TriggerAsync(new TriggerRequest
        {
            BackendId = "",
            SensationName = "compile_error_mild",
            ClientTraceId = "trace-empty",
        });

        response.Accepted.Should().BeFalse();
        response.Error.Code.Should().Be(TriggerErrorCode.BackendNotFound);
        response.ClientTraceId.Should().Be("trace-empty");
    }
}

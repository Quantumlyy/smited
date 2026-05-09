using System.Net.Http.Json;
using FluentAssertions;
using Smited.Daemon.Tests.Fixtures;
using Smited.V1;
using Xunit;

namespace Smited.Daemon.Tests.EndToEnd;

public class PanicEndpointTests : IDisposable
{
    private readonly DaemonFixture _fixture;

    public PanicEndpointTests()
    {
        _fixture = new DaemonFixture(seed: root =>
            SampleSensations.WriteOwo(root, "compile_error_mild.json", SampleSensations.CompileErrorMild));
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task POST_panic_with_no_active_sensations_returns_zero()
    {
        var response = await _fixture.PanicHttpClient.PostAsync("/panic", content: null);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<PanicResponse>();
        body!.Ok.Should().BeTrue();
        body.Stopped.Should().Be(0);
    }

    [Fact]
    public async Task GET_panic_responds_OK()
    {
        var response = await _fixture.PanicHttpClient.GetAsync("/panic");

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task POST_panic_cancels_an_active_sensation()
    {
        await _fixture.Client.TriggerAsync(new TriggerRequest
        {
            BackendId = "mock-owo",
            SensationName = "compile_error_mild",
            ClientTraceId = "trace-panic-test",
        });

        // Wait briefly for the trigger to settle.
        await Task.Delay(50);

        var response = await _fixture.PanicHttpClient.PostAsync("/panic", content: null);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<PanicResponse>();
        body!.Ok.Should().BeTrue();
        body.Stopped.Should().BeGreaterThanOrEqualTo(1);
    }

    private sealed record PanicResponse(bool Ok, int Stopped);
}

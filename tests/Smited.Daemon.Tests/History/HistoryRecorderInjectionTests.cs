using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Smited.Daemon.History;
using Smited.Daemon.Tests.Fixtures;
using Smited.V1;
using Xunit;

namespace Smited.Daemon.Tests.History;

/// <summary>
/// Verification tests proving the gRPC and panic call sites invoke the
/// recorder with the correct row shapes. Complements
/// <see cref="HistoryRecorderTests"/> (persistence) and
/// <see cref="EndToEnd.HistoryFlowTests"/> (rows land in the database) —
/// these check the wire-up between the call site and the recorder.
/// </summary>
public class HistoryRecorderInjectionTests
{
    [Fact]
    public async Task Trigger_call_invokes_RecordTriggerAsync_with_outcome()
    {
        var recorder = Substitute.For<IHistoryRecorder>();
        using var fixture = new DaemonFixture(
            seed: root => SampleSensations.WriteOwo(root, "compile_error_mild.json", SampleSensations.CompileErrorMild),
            configureServices: services =>
            {
                services.RemoveAll<IHistoryRecorder>();
                services.AddSingleton<IHistoryRecorder>(recorder);
            });

        await fixture.Client.TriggerAsync(new TriggerRequest
        {
            BackendId = "mock-owo",
            SensationName = "compile_error_mild",
            ClientTraceId = "trace-injection",
        });

        // Recorder calls are fire-and-forget; let the background task run.
        await Task.Delay(100);

        await recorder.Received(1).RecordTriggerAsync(
            Arg.Is<TriggerRecord>(r =>
                r.BackendId == "mock-owo" &&
                r.SensationName == "compile_error_mild" &&
                r.Accepted &&
                r.ClientTraceId == "trace-injection"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Panic_endpoint_invokes_both_RecordPanicAsync_and_RecordStopAsync()
    {
        var recorder = Substitute.For<IHistoryRecorder>();
        using var fixture = new DaemonFixture(
            configureServices: services =>
            {
                services.RemoveAll<IHistoryRecorder>();
                services.AddSingleton<IHistoryRecorder>(recorder);
            });

        var response = await fixture.PanicHttpClient.PostAsync("/panic", content: null);
        response.EnsureSuccessStatusCode();

        await Task.Delay(100);

        await recorder.Received(1).RecordPanicAsync(
            Arg.Is<PanicRecord>(p => p.Ok && p.Peer.Length > 0),
            Arg.Any<CancellationToken>());
        await recorder.Received(1).RecordStopAsync(
            Arg.Is<StopRecord>(s => s.Source == "panic" && s.All),
            Arg.Any<CancellationToken>());
    }
}

using FluentAssertions;
using Smited.Daemon.Admin.Components.Pages;
using Smited.Daemon.History;
using Xunit;

namespace Smited.Daemon.Tests.Admin;

public class RecentTriggersTests
{
    /// <summary>
    /// <see cref="TriggerRecord"/> is an acceptance record; the sensation
    /// may still be running, may have been cancelled, or may have failed
    /// after acceptance. Backfilling acceptance records as <c>Completed</c>
    /// is the bug from Round-N+1 fix #2; <c>Accepted</c> is the honest
    /// label. Live <c>SensationCompleted</c>/<c>SensationCancelled</c>
    /// events still produce the accurate completion-side rows.
    /// </summary>
    [Fact]
    public void Accepted_record_maps_to_Accepted_status_not_Completed()
    {
        var record = new TriggerRecord
        {
            Id = 1,
            BackendId = "mock-owo",
            SensationName = "compile_error_mild",
            SensationId = "abc123",
            Accepted = true,
            ClientTraceId = "trace-1",
        };

        var status = RecentTriggers.StatusFromRecord(record);

        status.Should().Be("Accepted");
        status.Should().NotBe("Completed");
    }

    [Fact]
    public void Rejected_record_maps_to_Rejected_status_not_Failed()
    {
        var record = new TriggerRecord
        {
            Id = 2,
            BackendId = "mock-owo",
            SensationName = "nonexistent",
            Accepted = false,
            ErrorCode = "SENSATION_NOT_FOUND",
            ClientTraceId = "trace-2",
        };

        var status = RecentTriggers.StatusFromRecord(record);

        status.Should().Be("Rejected");
        status.Should().NotBe("Failed");
    }
}

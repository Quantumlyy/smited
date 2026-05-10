// Excluded from compile on non-Windows hosts via the test csproj's
// conditional <Compile Remove>. References OwoBackend, which lives in
// the Windows-only Smited.Daemon.Owo assembly.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Smited.Daemon.Backends;
using Smited.Daemon.Owo;
using Xunit;

namespace Smited.Daemon.Tests.Backends;

public class OwoBackendAuthFilePathTests
{
    [Fact]
    public async Task ConnectAsync_with_invalid_AuthFilePath_throws_BackendConfigurationException()
    {
        var sdk = Substitute.For<IOwoSdk>();
        var options = new OwoBackendOptions
        {
            BackendId = "owo-test",
            ProjectId = "test",
            // Path under the well-known TEMP root that we just confirmed
            // doesn't exist; cross-platform readable as "definitely not
            // a real file" without hardcoding a Windows-specific path.
            AuthFilePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".owoauth"),
            ConnectTimeoutSeconds = 1,
        };
        var backend = new OwoBackend(
            options,
            sdk,
            TimeProvider.System,
            NullLogger<OwoBackend>.Instance);

        var act = () => backend.ConnectAsync(default);

        var thrown = await act.Should().ThrowAsync<BackendConfigurationException>();
        thrown.Which.BackendId.Should().Be("owo-test");
        thrown.Which.BackendKind.Should().Be("owo_skin");
        // SDK Configure should not have run because the auth-file
        // failure aborts before reaching Configure.
        sdk.DidNotReceive().Configure(Arg.Any<string>(), Arg.Any<string?>());
    }
}

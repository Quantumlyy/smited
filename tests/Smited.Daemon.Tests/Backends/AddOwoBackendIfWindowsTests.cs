// Excluded from compile on non-Windows hosts via the test csproj's
// conditional <Compile Remove>. AddOwoBackendIfWindows is a no-op
// off Windows; the assertions below would not run anywhere useful
// on Mac/Linux test runs.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Smited.Daemon.Backends;
using Xunit;

namespace Smited.Daemon.Tests.Backends;

public class AddOwoBackendIfWindowsTests
{
    [Fact]
    public void Loads_OwoBackendFactory_and_StaticOwoSdk_when_assembly_present()
    {
        var services = new ServiceCollection();

        services.AddOwoBackendIfWindows();

        services.Should().Contain(d =>
            d.ServiceType == typeof(IBackendFactory)
         && d.ImplementationType != null
         && d.ImplementationType.FullName == "Smited.Daemon.Owo.OwoBackendFactory");
        services.Should().Contain(d =>
            d.ServiceType == typeof(IOwoSdk)
         && d.ImplementationType != null
         && d.ImplementationType.FullName == "Smited.Daemon.Owo.StaticOwoSdk");
    }
}

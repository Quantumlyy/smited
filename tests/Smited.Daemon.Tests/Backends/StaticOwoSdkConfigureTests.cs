// Excluded from compile on non-Windows hosts via the test csproj's
// conditional <Compile Remove>. References StaticOwoSdk and the OWO
// SDK's GameAuth, both Windows-only.
//
// These are coverage tests for the StaticOwoSdk.Configure code path.
// The SDK's OWO.Configure(...) stashes the auth statically and is not
// observable from a test fixture, so the assertions are limited to
// "Configure didn't throw for representative inputs". Real verification
// of the GameAuth.Parse / GameAuth.Create branches happens during the
// Windows manual smoke runbook in docs/owo.md.

using Smited.Daemon.Owo;
using Xunit;

namespace Smited.Daemon.Tests.Backends;

public class StaticOwoSdkConfigureTests
{
    [Fact]
    public void Configure_with_null_authString_uses_GameAuth_Create_path()
    {
        // Hits the no-baked-sensations branch: GameAuth.Create().WithId(...).
        // Sufficient for the OWO Visualizer; not enough for MyOWO.
        new StaticOwoSdk().Configure("smited-dev", authString: null);
    }

    [Fact]
    public void Configure_with_authString_uses_GameAuth_Parse_path()
    {
        // Hits the GameAuth.Parse(authString).WithId(...) branch — the
        // production path required by the MyOWO consumer app. The string
        // below mirrors the example .owoauth payload format from the OWO
        // SDK docs (https://owo-game.gitbook.io/owo-api/welcome/configure-your-project);
        // the actual baked-sensation contents don't matter for the
        // coverage check.
        new StaticOwoSdk().Configure(
            "12345",
            "0~Ball~100,1,100,0,0,0,Impact|5%100~impact-0~");
    }
}

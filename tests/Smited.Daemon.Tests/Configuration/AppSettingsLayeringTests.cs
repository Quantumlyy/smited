using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Smited.Daemon.Configuration;
using Xunit;

namespace Smited.Daemon.Tests.Configuration;

/// <summary>
/// Regression tests for the .NET-config array-merge gotcha. Sample
/// descriptors in <c>appsettings.Development.json</c> previously leaked
/// into user configs that supplied only <c>Items[0]</c>: the layer
/// file's <c>Items[1]</c> survived the merge and produced duplicate-id
/// errors at startup. The fix is to keep the development sample's
/// <c>Items</c> empty and document the disabled-spare workflow as
/// user-config content. These tests pin that contract so anyone
/// tempted to "just add a sample back to appsettings.Development.json"
/// trips the regression marker before users do.
/// </summary>
public class AppSettingsLayeringTests
{
    [Fact]
    public void Development_appsettings_does_not_leak_default_Items_into_user_config()
    {
        // Reproduces the layer-merge bug: user config supplies a
        // single OWO descriptor at Items[0]; appsettings.Development
        // must not inject ghost descriptors at higher indexes.
        const string userConfig = """
        {
          "Smited": {
            "Backends": {
              "Items": [
                { "Kind": "owo_skin", "Id": "owo-primary", "Enabled": true }
              ]
            }
          }
        }
        """;

        var devConfigPath = ResolveDevConfigPath();

        var config = new ConfigurationBuilder()
            .AddJsonFile(devConfigPath, optional: false, reloadOnChange: false)
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(userConfig)))
            .Build();

        var bound = config.GetSection("Smited:Backends").Get<SmitedOptions.BackendsOptions>();

        bound.Should().NotBeNull();
        bound!.Items.Should().ContainSingle();
        bound.Items[0].Id.Should().Be("owo-primary");
        bound.Items[0].Enabled.Should().BeTrue();
    }

    [Fact]
    public void Development_appsettings_alone_yields_empty_Items()
    {
        // Without a user config layered on top, the development file
        // should bind to an empty Items array — the bootstrapper's
        // empty-Items fallback then synthesizes a default mock-owo
        // at startup. This locks down the intent that the dev file
        // ships no sample descriptors.
        var devConfigPath = ResolveDevConfigPath();

        var config = new ConfigurationBuilder()
            .AddJsonFile(devConfigPath, optional: false, reloadOnChange: false)
            .Build();

        var bound = config.GetSection("Smited:Backends").Get<SmitedOptions.BackendsOptions>();

        bound.Should().NotBeNull();
        bound!.Items.Should().BeEmpty();
    }

    private static string ResolveDevConfigPath()
    {
        // Resolve the source-tree appsettings.Development.json so the
        // test runs identically under Debug and Release configs (the
        // daemon's bin/{Configuration}/net9.0/ tree only exists for
        // the configuration that was just built, and the test
        // shouldn't depend on which one). AppContext.BaseDirectory
        // points at the test binary's output:
        //   tests/Smited.Daemon.Tests/bin/{Configuration}/net9.0/
        // Five "../" segments back to the repo root.
        var daemonSourceDir = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "Smited.Daemon");
        var path = Path.GetFullPath(Path.Combine(daemonSourceDir, "appsettings.Development.json"));
        File.Exists(path).Should().BeTrue(
            $"Resolved dev config path does not exist: {path}");
        return path;
    }
}

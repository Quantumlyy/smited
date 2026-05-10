using System.Text.Json;
using FluentAssertions;
using Smited.Daemon.Configuration;
using Xunit;

namespace Smited.Daemon.Tests.Configuration;

public class UserConfigPathTests : IDisposable
{
    private readonly string _tempRoot;

    public UserConfigPathTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "smited-userconfig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Resolve_returns_a_platform_appropriate_path()
    {
        // Make sure the env-var override doesn't leak from another test
        // and skew the platform-default check.
        var prior = Environment.GetEnvironmentVariable("SMITED_CONFIG_DIR");
        Environment.SetEnvironmentVariable("SMITED_CONFIG_DIR", null);
        try
        {
            var path = UserConfigPath.Resolve();

            path.Should().NotBeNullOrEmpty();
            Path.IsPathRooted(path).Should().BeTrue();
            path.Should().EndWith(Path.Combine("smited", "config.json"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SMITED_CONFIG_DIR", prior);
        }
    }

    [Fact]
    public void Resolve_honours_SMITED_CONFIG_DIR_override()
    {
        var prior = Environment.GetEnvironmentVariable("SMITED_CONFIG_DIR");
        Environment.SetEnvironmentVariable("SMITED_CONFIG_DIR", _tempRoot);
        try
        {
            UserConfigPath.Resolve().Should().Be(Path.Combine(_tempRoot, "config.json"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SMITED_CONFIG_DIR", prior);
        }
    }

    [Fact]
    public void EnsureExists_creates_the_directory_and_file_when_missing()
    {
        var path = Path.Combine(_tempRoot, "smited", "config.json");

        UserConfigPath.EnsureExists(path);

        File.Exists(path).Should().BeTrue();
        Directory.Exists(Path.GetDirectoryName(path)!).Should().BeTrue();
        File.ReadAllText(path).Should().Be(UserConfigPath.Template);
    }

    [Fact]
    public void EnsureExists_is_idempotent_and_does_not_overwrite()
    {
        var path = Path.Combine(_tempRoot, "smited", "config.json");
        UserConfigPath.EnsureExists(path);
        File.WriteAllText(path, "{ \"user-edit\": true }");
        var stamp = File.GetLastWriteTimeUtc(path);

        // Sleep briefly so a hypothetical re-write would change the timestamp.
        Thread.Sleep(10);
        UserConfigPath.EnsureExists(path);

        File.ReadAllText(path).Should().Be("{ \"user-edit\": true }");
        File.GetLastWriteTimeUtc(path).Should().Be(stamp);
    }

    [Fact]
    public void Template_parses_as_valid_JSON_with_comments()
    {
        var element = JsonSerializer.Deserialize<JsonElement>(UserConfigPath.Template, new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        element.ValueKind.Should().Be(JsonValueKind.Object);
    }
}

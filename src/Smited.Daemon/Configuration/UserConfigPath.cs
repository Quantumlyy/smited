namespace Smited.Daemon.Configuration;

/// <summary>
/// Resolves the platform-appropriate location for the user's smited
/// configuration overrides and ensures a template exists on first run.
/// </summary>
/// <remarks>
/// On Windows the file lives under <c>%APPDATA%\smited\config.json</c>.
/// On macOS and Linux it follows the XDG Base Directory specification,
/// defaulting to <c>$HOME/.config/smited/config.json</c> when
/// <c>XDG_CONFIG_HOME</c> is unset.
///
/// The user file is loaded after <c>appsettings.json</c> and
/// <c>appsettings.Development.json</c>, so any keys it sets win over
/// daemon defaults. The file is loaded as
/// <c>optional: true, reloadOnChange: false</c> — startup state
/// (registered backends, bound ports, loaded sensations) does not
/// reconcile cleanly with mid-flight config changes, and restarts
/// are cheap.
/// </remarks>
internal static class UserConfigPath
{
    /// <summary>
    /// Resolves the absolute path to the user configuration file for
    /// the current platform. The file is not guaranteed to exist; call
    /// <see cref="EnsureExists(string)"/> to materialize a template on
    /// first run.
    /// </summary>
    /// <remarks>
    /// Resolution order:
    /// <list type="number">
    ///   <item><c>SMITED_CONFIG_DIR</c> environment variable, if set —
    ///   <c>$SMITED_CONFIG_DIR/config.json</c>. Used by tests to redirect
    ///   away from the real user directory and by power users who keep
    ///   smited's config alongside other tooling.</item>
    ///   <item>Windows: <c>%APPDATA%\smited\config.json</c>.</item>
    ///   <item>macOS / Linux: <c>$XDG_CONFIG_HOME/smited/config.json</c>,
    ///   defaulting to <c>$HOME/.config/smited/config.json</c>.</item>
    /// </list>
    /// </remarks>
    /// <returns>An absolute path; non-empty.</returns>
    public static string Resolve()
    {
        var explicitDir = Environment.GetEnvironmentVariable("SMITED_CONFIG_DIR");
        if (!string.IsNullOrEmpty(explicitDir))
        {
            return Path.Combine(explicitDir, "config.json");
        }

        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "smited", "config.json");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var configHome = !string.IsNullOrEmpty(xdg)
            ? xdg
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        return Path.Combine(configHome, "smited", "config.json");
    }

    /// <summary>
    /// Creates the user configuration directory and writes a
    /// commented-out template to <paramref name="path"/> if no file
    /// exists there. Idempotent: calls after the first one are no-ops.
    /// </summary>
    /// <param name="path">
    /// Absolute path to the user config file, typically the result of
    /// <see cref="Resolve"/>.
    /// </param>
    public static void EnsureExists(string path)
    {
        if (File.Exists(path))
        {
            return;
        }

        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, Template);
    }

    /// <summary>
    /// First-run template content. Every key is commented out so the
    /// daemon's defaults from <c>appsettings.json</c> stay in effect
    /// until the operator chooses to override.
    /// </summary>
    internal const string Template = """
        {
          // smited user configuration
          //
          // Values here override appsettings.json defaults. Restart
          // the daemon after editing — config is not hot-reloaded.
          //
          // Uncomment and edit any of the following:
          //
          // "Smited": {
          //   "GrpcPort": 7777,
          //   "PanicPort": 7778,
          //   "BindAddress": "127.0.0.1",
          //   "EnableReflection": true,
          //   "Sensations": {
          //     "LibraryRoot": "./sensations"
          //   },
          //   "EventBus": {
          //     "BufferCapacity": 1024,
          //     "SlowSubscriberPolicy": "drop_oldest"
          //   }
          // }
        }
        """;
}

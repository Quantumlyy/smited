namespace Smited.Daemon.History;

/// <summary>
/// Resolves the platform-appropriate location for the SQLite history
/// database, honoring an optional override.
/// </summary>
/// <remarks>
/// Resolution order:
/// <list type="number">
///   <item>The <c>customPath</c> argument, if non-empty.</item>
///   <item>Windows: <c>%LOCALAPPDATA%\smited\history.db</c>.</item>
///   <item>macOS / Linux: <c>$XDG_DATA_HOME/smited/history.db</c>,
///   defaulting to <c>$HOME/.local/share/smited/history.db</c>.</item>
/// </list>
/// </remarks>
internal static class HistoryDbPathResolver
{
    public static string Resolve(string? customPath)
    {
        if (!string.IsNullOrEmpty(customPath))
        {
            return Path.GetFullPath(customPath);
        }

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "smited", "history.db");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var dataHome = !string.IsNullOrEmpty(xdg)
            ? xdg
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share");
        return Path.Combine(dataHome, "smited", "history.db");
    }
}

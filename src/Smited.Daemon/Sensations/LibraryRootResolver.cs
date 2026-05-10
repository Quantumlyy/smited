namespace Smited.Daemon.Sensations;

/// <summary>
/// Resolves the configured sensation <c>LibraryRoot</c> to an absolute
/// path. Relative paths are anchored to <see cref="AppContext.BaseDirectory"/>
/// so the daemon's default <c>./sensations</c> resolves to the directory
/// next to the binary regardless of the invoker's working directory.
/// </summary>
internal static class LibraryRootResolver
{
    public static string Resolve(string configured)
    {
        if (string.IsNullOrEmpty(configured) || Path.IsPathRooted(configured))
        {
            return configured;
        }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured));
    }
}

namespace Smited.Daemon.Sensations;

/// <summary>
/// Thrown by hosted services that fail their <c>StartAsync</c> in a way that
/// should abort the entire host (corrupt sensation files, missing required
/// directories, etc.). The host's default behaviour on a hosted-service
/// throw is to bring the application down, which is what we want.
/// </summary>
public sealed class SmitedStartupException : Exception
{
    public SmitedStartupException(string message) : base(message) { }
    public SmitedStartupException(string message, Exception inner) : base(message, inner) { }
}

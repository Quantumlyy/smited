namespace Smited.Daemon.Backends;

/// <summary>
/// Thrown by a backend's <c>ConnectAsync</c> when its configuration is
/// fundamentally unusable in this environment (e.g. an
/// <c>AuthFilePath</c> that can't be read, an obviously-malformed
/// option). Carrying <see cref="BackendId"/> and <see cref="BackendKind"/>
/// lets the bootstrapper attribute the failure without having to inspect
/// the live backend instance, which may not be in a coherent state by
/// the time the exception surfaces.
/// </summary>
/// <remarks>
/// Modelled on <c>SmitedStartupException</c>: the host's default
/// behaviour on a hosted-service throw is to bring the application down,
/// which is the right response to misconfiguration the user must fix.
/// Lives in the abstractions assembly so platform-conditional backends
/// (e.g. <c>OwoBackend</c>) can throw it without forcing a circular
/// dependency.
/// </remarks>
public sealed class BackendConfigurationException : Exception
{
    public string BackendId { get; }
    public string BackendKind { get; }

    public BackendConfigurationException(string backendId, string backendKind, string message)
        : base(message)
    {
        BackendId = backendId;
        BackendKind = backendKind;
    }

    public BackendConfigurationException(string backendId, string backendKind, string message, Exception inner)
        : base(message, inner)
    {
        BackendId = backendId;
        BackendKind = backendKind;
    }
}

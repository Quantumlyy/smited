namespace Smited.Daemon.Backends;

/// <summary>
/// Thrown by an <see cref="IBackendFactory"/> when a descriptor's
/// <c>Options</c> section is malformed in a way the user must fix —
/// missing required field, out-of-range value, contradictory
/// combinations — and by a backend's <c>ConnectAsync</c> when its
/// configuration is fundamentally unusable in this environment (e.g.
/// an <c>AuthFilePath</c> that can't be read). Carrying
/// <see cref="BackendId"/> and <see cref="BackendKind"/> lets the
/// bootstrapper attribute the failure without having to inspect the
/// live backend instance, which may not be in a coherent state by
/// the time the exception surfaces.
/// </summary>
/// <remarks>
/// <para>
/// Modelled on <c>SmitedStartupException</c>: the host's default
/// behaviour on a hosted-service throw is to bring the application
/// down, which is the right response to misconfiguration the user
/// must fix. Distinct from a <c>null</c> return value (which signals
/// "this environment can't host the backend, skip and continue") and
/// from arbitrary <see cref="Exception"/> subclasses (which the
/// bootstrapper rewraps into a generic startup failure). A
/// <see cref="BackendConfigurationException"/> says the user typed
/// something wrong; the daemon refuses to start until they fix it.
/// </para>
/// <para>
/// Lives in the abstractions assembly so platform-conditional
/// backends (e.g. <c>OwoBackend</c>) can throw it without forcing a
/// circular dependency.
/// </para>
/// </remarks>
public sealed class BackendConfigurationException : Exception
{
    /// <summary>The backend / descriptor id whose configuration failed.</summary>
    public string BackendId { get; }

    /// <summary>The backend / descriptor kind whose configuration failed.</summary>
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

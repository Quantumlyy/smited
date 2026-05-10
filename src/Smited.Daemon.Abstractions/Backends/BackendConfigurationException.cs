namespace Smited.Daemon.Backends;

/// <summary>
/// Thrown by an <see cref="IBackendFactory"/> when a descriptor's
/// <c>Options</c> section is malformed in a way the user must fix —
/// missing required field, out-of-range value, contradictory
/// combinations, etc.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from a <c>null</c> return value (which signals "this
/// environment can't host the backend, skip and continue") and from
/// arbitrary <see cref="Exception"/> subclasses (which the bootstrapper
/// rewraps into a generic startup failure). A
/// <see cref="BackendConfigurationException"/> says the user typed
/// something wrong; the daemon refuses to start until they fix it. The
/// bootstrapper logs the descriptor id and kind alongside the exception
/// message.
/// </para>
/// <para>
/// <see cref="DescriptorId"/> and <see cref="Kind"/> are captured on the
/// exception so log handlers and tests can match the failure to the
/// specific descriptor without parsing the message string.
/// </para>
/// </remarks>
public sealed class BackendConfigurationException : Exception
{
    /// <summary>The descriptor id whose options failed validation.</summary>
    public string DescriptorId { get; }

    /// <summary>The descriptor kind whose options failed validation.</summary>
    public string Kind { get; }

    public BackendConfigurationException(string descriptorId, string kind, string message)
        : base($"Backend descriptor '{descriptorId}' (kind '{kind}'): {message}")
    {
        DescriptorId = descriptorId;
        Kind = kind;
    }

    public BackendConfigurationException(string descriptorId, string kind, string message, Exception inner)
        : base($"Backend descriptor '{descriptorId}' (kind '{kind}'): {message}", inner)
    {
        DescriptorId = descriptorId;
        Kind = kind;
    }
}

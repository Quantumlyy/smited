namespace Smited.Daemon.Pishock;

/// <summary>
/// Transport selector for a PiShock backend descriptor. Each shocker
/// picks one mode in its <see cref="PishockBackendOptions.Mode"/>; a
/// daemon can run cloud-mode and LAN-mode shockers side by side.
/// </summary>
public enum PishockTransportMode
{
    /// <summary>
    /// Authenticated HTTPS to <c>do.pishock.com/api/apioperate</c> using
    /// username + API key + share code. Works anywhere the daemon has
    /// outbound internet; duration resolution is one second (the cloud
    /// API's minimum) so unsuitable for sub-second staccato patterns.
    /// </summary>
    Cloud,

    /// <summary>
    /// Direct HTTP to the device on the local network using its IP
    /// address. No auth (the device trusts whoever can reach it).
    /// Sub-second duration control; preferred for fast pattern playback.
    /// </summary>
    Lan,
}

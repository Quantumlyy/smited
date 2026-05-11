namespace Smited.Daemon.Backends;

/// <summary>
/// Daemon-wide settings for the bHaptics backend family. Bound from the
/// <c>Smited:Bhaptics</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="BhapticsBackendOptionsBase"/> (and its
/// derivatives) because the bHaptics SDK is a process-wide singleton:
/// every <c>bhaptics_*</c> backend talks through the same
/// <c>HapticPlayer</c>, and the SDK's identity ("AppId") is decided
/// once at first <c>InitializeAsync</c>. Promoting AppId to a
/// daemon-global setting eliminates the silent ambiguity where two
/// backend descriptors disagree on AppId and the first caller wins
/// invisibly.
/// </para>
/// </remarks>
public sealed class BhapticsGlobalOptions
{
    /// <summary>
    /// Free-form smited-side identifier emitted into log lines for the
    /// bHaptics SDK init/connection events. SDK1 (<c>Bhaptics.Tac</c>
    /// 1.4.2) does NOT accept an app identifier in its <c>HapticPlayer</c>
    /// constructor — the Developer Portal app-ID flow is SDK2-only, and
    /// smited stays on SDK1 deliberately. The value lives here so
    /// future SDK upgrades that re-introduce an app-ID surface have a
    /// single bound config field to read from, instead of needing a
    /// new <c>BackendOptions</c> field on each of five backend kinds.
    /// Default <c>"smited"</c>.
    /// </summary>
    public string AppId { get; set; } = "smited";
}

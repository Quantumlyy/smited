using Smited.Daemon.Backends;

namespace Smited.Daemon.Configuration;

/// <summary>
/// Strongly-typed bindings for the <c>Smited</c> configuration section. Maps
/// 1:1 onto <c>appsettings.json</c>.
/// </summary>
public sealed class SmitedOptions
{
    /// <summary>TCP port for the gRPC h2c listener.</summary>
    public int GrpcPort { get; set; } = 7777;

    /// <summary>
    /// TCP port for the emergency-stop HTTP/1.1 listener. Separate from the
    /// gRPC listener so a wedged gRPC pipeline can't take the panic button
    /// down with it.
    /// </summary>
    public int PanicPort { get; set; } = 7778;

    /// <summary>
    /// Address Kestrel binds to. Default <c>127.0.0.1</c> means the daemon
    /// is reachable only from the same machine — including the panic
    /// endpoint. Flip to <c>0.0.0.0</c> to expose on the LAN.
    /// </summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>Whether to map <c>grpc.reflection.v1alpha.ServerReflection</c>.</summary>
    public bool EnableReflection { get; set; } = true;

    public BackendsOptions Backends { get; set; } = new();

    public SensationsOptions Sensations { get; set; } = new();

    public EventBusOptions EventBus { get; set; } = new();

    public HistoryOptions History { get; set; } = new();

    public sealed class BackendsOptions
    {
        /// <summary>
        /// Backend descriptors to bring online at startup. Each entry
        /// names a kind and an instance id; the daemon resolves the
        /// matching <c>IBackendFactory</c> and lets it construct the
        /// backend. Per-instance configuration sits under the entry's
        /// <c>Options</c> sub-section.
        /// </summary>
        /// <remarks>
        /// Lands in Commit 1 alongside the legacy boolean knobs. The
        /// bootstrapper does not consume <see cref="Items"/> until
        /// Commit 2; Commit 1 simply makes the new shape bindable so
        /// tests covering descriptor binding can be written.
        /// </remarks>
        public List<BackendDescriptor> Items { get; set; } = new();

        public bool EnableMockOwo { get; set; } = true;
        public bool EnableOwo { get; set; }

        /// <summary>
        /// Configuration for the real OWO Skin backend, used when
        /// <see cref="EnableOwo"/> is <c>true</c> on a Windows host.
        /// </summary>
        public OwoBackendOptions Owo { get; set; } = new();
    }

    public sealed class SensationsOptions
    {
        public string LibraryRoot { get; set; } = "./sensations";
        public bool WatchForChanges { get; set; }
    }

    public sealed class EventBusOptions
    {
        public int BufferCapacity { get; set; } = 1024;
        public string SlowSubscriberPolicy { get; set; } = "drop_oldest";
    }

    public sealed class HistoryOptions
    {
        /// <summary>
        /// When <c>false</c>, the daemon registers a no-op recorder and skips
        /// creating or opening the history database. Useful for tests and for
        /// users who don't want a database file.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Number of days to retain rows. Set to <c>0</c> to keep history
        /// forever (database grows unbounded). Default 30.
        /// </summary>
        public int RetentionDays { get; set; } = 30;

        /// <summary>
        /// Optional override for the database path. When unset, the daemon
        /// resolves a platform-appropriate location under
        /// <c>$XDG_DATA_HOME/smited/history.db</c> or
        /// <c>%LOCALAPPDATA%\smited\history.db</c>.
        /// </summary>
        public string? CustomPath { get; set; }
    }
}

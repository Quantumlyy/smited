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

    public sealed class BackendsOptions
    {
        public bool EnableMockOwo { get; set; } = true;
        public bool EnableOwo { get; set; }
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
}

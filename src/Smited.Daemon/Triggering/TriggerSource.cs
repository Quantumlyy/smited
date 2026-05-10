using Smited.Daemon.History;

namespace Smited.Daemon.Triggering;

/// <summary>
/// Where an action was initiated. Carried through the
/// <see cref="SmitedActionService"/> facade so logs and the
/// <see cref="StopRecord.Source"/> column can distinguish "client
/// called gRPC" from "operator clicked admin UI button" from "panic HTTP
/// endpoint hit". Persisting the source on
/// <see cref="TriggerRecord"/> and <see cref="PanicRecord"/>
/// is a follow-up — adding columns there requires a real migration story
/// (the daemon currently uses <c>EnsureCreated</c>, which doesn't add new
/// columns to existing databases).
/// </summary>
internal enum TriggerSource
{
    Grpc,
    PanicHttp,
    Admin,
}

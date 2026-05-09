using Google.Protobuf;
using Grpc.Core;
using Grpc.Core.Interceptors;
using ProtoValidate;

namespace Smited.Daemon.Validation;

/// <summary>
/// Server interceptor that runs the <c>buf.validate</c> annotations against
/// every incoming gRPC request before delegating to the handler. Violations
/// surface as <c>INVALID_ARGUMENT</c> with a human-readable message listing
/// every failed rule. Domain-level rejections (unknown zone, parameter type
/// mismatch, etc.) are <em>not</em> handled here — those are success-shaped
/// <c>TriggerResponse{accepted=false, error=...}</c> outcomes.
/// </summary>
internal sealed class ProtovalidateInterceptor : Interceptor
{
    private readonly IValidator _validator;

    public ProtovalidateInterceptor(IValidator validator)
    {
        _validator = validator;
    }

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        ValidateOrThrow(request);
        return continuation(request, context);
    }

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        ValidateOrThrow(request);
        return continuation(request, responseStream, context);
    }

    private void ValidateOrThrow<TRequest>(TRequest request)
    {
        if (request is not IMessage message)
        {
            return;
        }

        var result = _validator.Validate(message, failFast: false);
        if (result.Violations.Count == 0)
        {
            return;
        }

        var lines = result.Violations.Select(FormatViolation);
        var message_ = "request failed validation: " + string.Join("; ", lines);
        throw new RpcException(new Status(StatusCode.InvalidArgument, message_));
    }

    private static string FormatViolation(Buf.Validate.Violation v)
    {
        var path = v.Field?.Elements?.Count > 0
            ? string.Join(".", v.Field.Elements.Select(e => e.FieldName))
            : "<message>";
        return $"{path}: {v.Message}";
    }
}

using Grpc.Core;
using Monitor.Web.Auth;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace Monitor.Web.Otlp;

public sealed class OtlpTraceGrpcService(
    OtlpIngestionProcessor processor,
    IngestionCredentialAuthenticator authenticator)
    : TraceService.TraceServiceBase
{
    public override async Task<ExportTraceServiceResponse> Export(
        ExportTraceServiceRequest request,
        ServerCallContext context)
    {
        var identity = await OtlpGrpcAuthentication.AuthenticateAsync(authenticator, context);
        var result = await processor.ProcessAsync(request, identity.ComponentId, context.CancellationToken);
        if (!result.Allowed)
        {
            throw OtlpGrpcAuthentication.PermissionDenied();
        }

        return result.Response!;
    }
}

public sealed class OtlpLogsGrpcService(
    OtlpIngestionProcessor processor,
    IngestionCredentialAuthenticator authenticator)
    : LogsService.LogsServiceBase
{
    public override async Task<ExportLogsServiceResponse> Export(
        ExportLogsServiceRequest request,
        ServerCallContext context)
    {
        var identity = await OtlpGrpcAuthentication.AuthenticateAsync(authenticator, context);
        var result = await processor.ProcessAsync(request, identity.ComponentId, context.CancellationToken);
        if (!result.Allowed)
        {
            throw OtlpGrpcAuthentication.PermissionDenied();
        }

        return result.Response!;
    }
}

public sealed class OtlpMetricsGrpcService(
    OtlpIngestionProcessor processor,
    IngestionCredentialAuthenticator authenticator)
    : MetricsService.MetricsServiceBase
{
    public override async Task<ExportMetricsServiceResponse> Export(
        ExportMetricsServiceRequest request,
        ServerCallContext context)
    {
        var identity = await OtlpGrpcAuthentication.AuthenticateAsync(authenticator, context);
        var result = await processor.ProcessAsync(request, identity.ComponentId, context.CancellationToken);
        if (!result.Allowed)
        {
            throw OtlpGrpcAuthentication.PermissionDenied();
        }

        return result.Response!;
    }
}

file static class OtlpGrpcAuthentication
{
    public static async Task<IngestionIdentity> AuthenticateAsync(
        IngestionCredentialAuthenticator authenticator,
        ServerCallContext context)
    {
        var identity = await authenticator.AuthenticateAsync(
            context.GetHttpContext(),
            allowOperator: false,
            context.CancellationToken);

        return identity ?? throw new RpcException(new Status(
            StatusCode.Unauthenticated,
            "A valid Monitor ingestion credential is required."));
    }

    public static RpcException PermissionDenied() => new(new Status(
        StatusCode.PermissionDenied,
        "The ingestion credential is not scoped to the OTLP resource component."));
}

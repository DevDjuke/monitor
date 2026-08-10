using System.IO.Compression;
using Google.Protobuf;
using Monitor.Web.Auth;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace Monitor.Web.Otlp;

public static class OtlpEndpoints
{
    public static IEndpointRouteBuilder MapOtlp(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v1/traces", ImportTraces);
        endpoints.MapPost("/v1/logs", ImportLogs);
        return endpoints;
    }

    private static async Task<IResult> ImportTraces(
        HttpContext httpContext,
        OtlpTraceImporter importer,
        OtlpComponentScopeValidator scopeValidator,
        IngestionCredentialAuthenticator authenticator,
        CancellationToken cancellationToken)
    {
        var identity = await authenticator.AuthenticateAsync(
            httpContext,
            allowOperator: false,
            cancellationToken);
        if (identity is null)
        {
            return Results.Unauthorized();
        }

        if (!IsProtobuf(httpContext.Request.ContentType))
        {
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        var payload = await ReadPayloadAsync(httpContext.Request, cancellationToken);
        if (payload is null)
        {
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        ExportTraceServiceRequest request;
        try
        {
            request = ExportTraceServiceRequest.Parser.ParseFrom(payload);
        }
        catch (InvalidProtocolBufferException)
        {
            return Results.BadRequest();
        }

        if (!await scopeValidator.CanIngestAsync(request, identity.ComponentId, cancellationToken))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var result = await importer.ImportAsync(request, cancellationToken);
        var response = new ExportTraceServiceResponse();
        if (result.RejectedSpans > 0)
        {
            response.PartialSuccess = new ExportTracePartialSuccess
            {
                RejectedSpans = result.RejectedSpans,
                ErrorMessage = "Some spans were rejected because trace_id or span_id was invalid."
            };
        }

        return Results.Bytes(response.ToByteArray(), "application/x-protobuf");
    }

    private static async Task<IResult> ImportLogs(
        HttpContext httpContext,
        OtlpLogImporter importer,
        OtlpComponentScopeValidator scopeValidator,
        IngestionCredentialAuthenticator authenticator,
        CancellationToken cancellationToken)
    {
        var identity = await authenticator.AuthenticateAsync(
            httpContext,
            allowOperator: false,
            cancellationToken);
        if (identity is null)
        {
            return Results.Unauthorized();
        }

        if (!IsProtobuf(httpContext.Request.ContentType))
        {
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        var payload = await ReadPayloadAsync(httpContext.Request, cancellationToken);
        if (payload is null)
        {
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        ExportLogsServiceRequest request;
        try
        {
            request = ExportLogsServiceRequest.Parser.ParseFrom(payload);
        }
        catch (InvalidProtocolBufferException)
        {
            return Results.BadRequest();
        }

        if (!await scopeValidator.CanIngestAsync(request, identity.ComponentId, cancellationToken))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var result = await importer.ImportAsync(request, cancellationToken);
        var response = new ExportLogsServiceResponse();
        if (result.RejectedLogs > 0)
        {
            response.PartialSuccess = new ExportLogsPartialSuccess
            {
                RejectedLogRecords = result.RejectedLogs,
                ErrorMessage = "Some log records could not be accepted."
            };
        }

        return Results.Bytes(response.ToByteArray(), "application/x-protobuf");
    }

    private static bool IsProtobuf(string? contentType)
    {
        var normalized = contentType?.Split(';', 2)[0].Trim();
        return string.Equals(normalized, "application/x-protobuf", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "application/protobuf", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]?> ReadPayloadAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        Stream input = request.Body;
        GZipStream? gzip = null;
        var contentEncoding = request.Headers.ContentEncoding.ToString();
        if (!string.IsNullOrWhiteSpace(contentEncoding))
        {
            if (!string.Equals(contentEncoding, "gzip", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
            input = gzip;
        }

        try
        {
            await using var payload = new MemoryStream();
            await input.CopyToAsync(payload, cancellationToken);
            return payload.ToArray();
        }
        finally
        {
            if (gzip is not null)
            {
                await gzip.DisposeAsync();
            }
        }
    }
}

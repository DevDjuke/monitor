using System.IO.Compression;
using Google.Protobuf;
using Monitor.Web.Auth;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace Monitor.Web.Otlp;

public static class OtlpEndpoints
{
    public static IEndpointRouteBuilder MapOtlp(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v1/traces", ImportTraces);
        return endpoints;
    }

    private static async Task<IResult> ImportTraces(
        HttpContext httpContext,
        OtlpTraceImporter importer,
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

        var contentType = httpContext.Request.ContentType?.Split(';', 2)[0].Trim();
        if (!string.Equals(contentType, "application/x-protobuf", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(contentType, "application/protobuf", StringComparison.OrdinalIgnoreCase))
        {
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        Stream input = httpContext.Request.Body;
        GZipStream? gzip = null;
        var contentEncoding = httpContext.Request.Headers.ContentEncoding.ToString();
        if (!string.IsNullOrWhiteSpace(contentEncoding))
        {
            if (!string.Equals(contentEncoding, "gzip", StringComparison.OrdinalIgnoreCase))
            {
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            }

            gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
            input = gzip;
        }

        try
        {
            await using var payload = new MemoryStream();
            await input.CopyToAsync(payload, cancellationToken);
            payload.Position = 0;

            ExportTraceServiceRequest request;
            try
            {
                request = ExportTraceServiceRequest.Parser.ParseFrom(payload);
            }
            catch (InvalidProtocolBufferException)
            {
                return Results.BadRequest();
            }

            OtlpImportResult result;
            try
            {
                result = await importer.ImportAsync(request, identity.ComponentId, cancellationToken);
            }
            catch (OtlpComponentScopeException)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

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
        finally
        {
            if (gzip is not null)
            {
                await gzip.DisposeAsync();
            }
        }
    }
}

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
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
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var expectedApiKey = configuration["Monitor:IngestionApiKey"];
        if (string.IsNullOrWhiteSpace(expectedApiKey))
        {
            return Results.Json(
                new { error = "The ingestion API key is not configured." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var suppliedApiKey = httpContext.Request.Headers["X-Monitor-Key"].ToString();
        if (!ApiKeysMatch(expectedApiKey, suppliedApiKey))
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
        finally
        {
            if (gzip is not null)
            {
                await gzip.DisposeAsync();
            }
        }
    }

    private static bool ApiKeysMatch(string expected, string supplied)
    {
        if (string.IsNullOrEmpty(supplied)) return false;
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        return CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash);
    }
}

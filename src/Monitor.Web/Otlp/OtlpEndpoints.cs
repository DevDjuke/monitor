using System.IO.Compression;
using System.Text;
using Google.Protobuf;
using Monitor.Web.Auth;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace Monitor.Web.Otlp;

public static class OtlpEndpoints
{
    public static IEndpointRouteBuilder MapOtlp(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v1/traces", ImportTraces);
        endpoints.MapPost("/v1/logs", ImportLogs);
        endpoints.MapPost("/v1/metrics", ImportMetrics);
        return endpoints;
    }

    private static async Task<IResult> ImportTraces(
        HttpContext httpContext,
        OtlpIngestionProcessor processor,
        IngestionCredentialAuthenticator authenticator,
        CancellationToken cancellationToken)
    {
        var identity = await AuthenticateAsync(httpContext, authenticator, cancellationToken);
        if (identity is null)
        {
            return Results.Unauthorized();
        }

        if (!TryGetEncoding(httpContext.Request.ContentType, out var encoding))
        {
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        var request = await ReadRequestAsync(httpContext.Request, ExportTraceServiceRequest.Parser, encoding, cancellationToken);
        if (request is null)
        {
            return Results.BadRequest();
        }

        var result = await processor.ProcessAsync(request, identity.ComponentId, cancellationToken);
        return result.Allowed
            ? WriteResponse(result.Response!, encoding)
            : Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    private static async Task<IResult> ImportLogs(
        HttpContext httpContext,
        OtlpIngestionProcessor processor,
        IngestionCredentialAuthenticator authenticator,
        CancellationToken cancellationToken)
    {
        var identity = await AuthenticateAsync(httpContext, authenticator, cancellationToken);
        if (identity is null)
        {
            return Results.Unauthorized();
        }

        if (!TryGetEncoding(httpContext.Request.ContentType, out var encoding))
        {
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        var request = await ReadRequestAsync(httpContext.Request, ExportLogsServiceRequest.Parser, encoding, cancellationToken);
        if (request is null)
        {
            return Results.BadRequest();
        }

        var result = await processor.ProcessAsync(request, identity.ComponentId, cancellationToken);
        return result.Allowed
            ? WriteResponse(result.Response!, encoding)
            : Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    private static async Task<IResult> ImportMetrics(
        HttpContext httpContext,
        OtlpIngestionProcessor processor,
        IngestionCredentialAuthenticator authenticator,
        CancellationToken cancellationToken)
    {
        var identity = await AuthenticateAsync(httpContext, authenticator, cancellationToken);
        if (identity is null)
        {
            return Results.Unauthorized();
        }

        if (!TryGetEncoding(httpContext.Request.ContentType, out var encoding))
        {
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        var request = await ReadRequestAsync(httpContext.Request, ExportMetricsServiceRequest.Parser, encoding, cancellationToken);
        if (request is null)
        {
            return Results.BadRequest();
        }

        var result = await processor.ProcessAsync(request, identity.ComponentId, cancellationToken);
        return result.Allowed
            ? WriteResponse(result.Response!, encoding)
            : Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    private static Task<IngestionIdentity?> AuthenticateAsync(
        HttpContext httpContext,
        IngestionCredentialAuthenticator authenticator,
        CancellationToken cancellationToken) =>
        authenticator.AuthenticateAsync(httpContext, allowOperator: false, cancellationToken);

    private static bool TryGetEncoding(string? contentType, out OtlpHttpEncoding encoding)
    {
        var normalized = contentType?.Split(';', 2)[0].Trim();
        if (string.Equals(normalized, "application/x-protobuf", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "application/protobuf", StringComparison.OrdinalIgnoreCase))
        {
            encoding = OtlpHttpEncoding.Protobuf;
            return true;
        }

        if (string.Equals(normalized, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            encoding = OtlpHttpEncoding.Json;
            return true;
        }

        encoding = default;
        return false;
    }

    private static async Task<T?> ReadRequestAsync<T>(
        HttpRequest request,
        MessageParser<T> parser,
        OtlpHttpEncoding encoding,
        CancellationToken cancellationToken)
        where T : class, IMessage<T>, new()
    {
        var payload = await ReadPayloadAsync(request, cancellationToken);
        if (payload is null)
        {
            return null;
        }

        try
        {
            return encoding switch
            {
                OtlpHttpEncoding.Protobuf => parser.ParseFrom(payload),
                OtlpHttpEncoding.Json => JsonParser.Default.Parse<T>(Encoding.UTF8.GetString(payload)),
                _ => null
            };
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or InvalidJsonException)
        {
            return null;
        }
    }

    private static IResult WriteResponse(IMessage response, OtlpHttpEncoding encoding) =>
        encoding switch
        {
            OtlpHttpEncoding.Protobuf => Results.Bytes(response.ToByteArray(), "application/x-protobuf"),
            OtlpHttpEncoding.Json => Results.Text(JsonFormatter.Default.Format(response), "application/json", Encoding.UTF8),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
        };

    private static async Task<byte[]?> ReadPayloadAsync(HttpRequest request, CancellationToken cancellationToken)
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
        catch (InvalidDataException)
        {
            return null;
        }
        finally
        {
            if (gzip is not null)
            {
                await gzip.DisposeAsync();
            }
        }
    }

    private enum OtlpHttpEncoding
    {
        Protobuf,
        Json
    }
}

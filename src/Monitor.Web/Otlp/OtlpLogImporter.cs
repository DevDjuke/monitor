using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;

namespace Monitor.Web.Otlp;

public sealed partial class OtlpLogImporter(
    MonitorDbContext db,
    ILogger<OtlpLogImporter> logger)
{
    private const string ImportLockResource = "Monitor.OtlpLogImport";

    public async Task<OtlpLogImportResult> ImportAsync(
        ExportLogsServiceRequest request,
        CancellationToken cancellationToken)
    {
        var accepted = 0;
        var rejected = 0L;
        var duplicates = 0;

        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            if (!await TryAcquireImportLockAsync(db.Database.GetDbConnection(), cancellationToken))
            {
                throw new TimeoutException("Monitor could not acquire the OTLP log ingestion lock within 15 seconds.");
            }

            try
            {
                foreach (var resourceLogs in request.ResourceLogs)
                {
                    var resourceAttributes = ToDictionary(resourceLogs.Resource?.Attributes ?? []);
                    var serviceName = GetString(resourceAttributes, "service.name") ?? "unknown_service";
                    var serviceNamespace = GetString(resourceAttributes, "service.namespace");
                    var environment = GetString(resourceAttributes, "deployment.environment.name")
                        ?? GetString(resourceAttributes, "deployment.environment")
                        ?? "unknown";
                    var version = GetString(resourceAttributes, "service.version");
                    var slug = BuildSlug(serviceNamespace, serviceName);
                    var now = DateTimeOffset.UtcNow;

                    var component = await GetOrCreateComponentAsync(
                        serviceName,
                        slug,
                        environment,
                        version,
                        now,
                        cancellationToken);
                    component.Heartbeat(now);

                    foreach (var scopeLogs in resourceLogs.ScopeLogs)
                    {
                        var source = string.IsNullOrWhiteSpace(scopeLogs.Scope?.Name)
                            ? null
                            : scopeLogs.Scope.Name.Trim();

                        foreach (var record in scopeLogs.LogRecords)
                        {
                            var attributes = ToDictionary(record.Attributes);
                            var traceId = record.TraceId.Length == 16 ? ToHex(record.TraceId) : null;
                            var spanExternalId = record.SpanId.Length == 8 ? ToHex(record.SpanId) : null;
                            var timestamp = FromUnixNano(record.TimeUnixNano != 0
                                ? record.TimeUnixNano
                                : record.ObservedTimeUnixNano);
                            var observedAt = FromUnixNano(record.ObservedTimeUnixNano != 0
                                ? record.ObservedTimeUnixNano
                                : record.TimeUnixNano);

                            var run = traceId is null
                                ? null
                                : await db.Runs
                                    .AsNoTracking()
                                    .Where(x => x.ComponentId == component.Id && x.TraceId == traceId)
                                    .Select(x => new { x.Id })
                                    .SingleOrDefaultAsync(cancellationToken);

                            Guid? spanId = null;
                            if (run is not null && spanExternalId is not null)
                            {
                                spanId = await db.Spans
                                    .AsNoTracking()
                                    .Where(x => x.RunId == run.Id && x.ExternalSpanId == spanExternalId)
                                    .Select(x => (Guid?)x.Id)
                                    .SingleOrDefaultAsync(cancellationToken);
                            }

                            var bodyObject = ToObject(record.Body);
                            var message = bodyObject switch
                            {
                                null => string.IsNullOrWhiteSpace(record.EventName) ? "(empty log record)" : record.EventName,
                                string text => text,
                                _ => JsonSerializer.Serialize(bodyObject)
                            };
                            var propertiesJson = attributes.Count == 0
                                ? null
                                : JsonSerializer.Serialize(attributes);
                            var messageTemplate = FirstNonEmpty(
                                GetString(attributes, "{OriginalFormat}"),
                                GetString(attributes, "OriginalFormat"),
                                GetString(attributes, "message.template"));
                            var externalRecordId = GetString(attributes, "log.record.uid");
                            var exceptionType = GetString(attributes, "exception.type");
                            var exceptionMessage = GetString(attributes, "exception.message");
                            var exceptionStackTrace = GetString(attributes, "exception.stacktrace");
                            var level = MapLevel((int)record.SeverityNumber);
                            var dedupeKey = BuildDedupeKey(
                                component.Id,
                                externalRecordId,
                                source,
                                traceId,
                                spanExternalId,
                                record.EventName,
                                record.SeverityText,
                                (int)record.SeverityNumber,
                                record.TimeUnixNano,
                                record.ObservedTimeUnixNano,
                                message,
                                propertiesJson);

                            if (await db.LogEvents.AnyAsync(x => x.DedupeKey == dedupeKey, cancellationToken))
                            {
                                duplicates++;
                                continue;
                            }

                            var logEvent = LogEvent.CreateOtlp(
                                component.Id,
                                run?.Id,
                                spanId,
                                traceId,
                                spanExternalId,
                                externalRecordId,
                                dedupeKey,
                                level,
                                record.SeverityText,
                                timestamp,
                                observedAt,
                                message,
                                messageTemplate,
                                propertiesJson,
                                exceptionType,
                                exceptionMessage,
                                exceptionStackTrace,
                                source,
                                record.EventName,
                                now);

                            db.LogEvents.Add(logEvent);
                            accepted++;
                        }
                    }
                }

                await db.SaveChangesAsync(cancellationToken);
            }
            finally
            {
                await ReleaseImportLockAsync(db.Database.GetDbConnection(), cancellationToken);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        logger.LogDebug(
            "Imported {AcceptedLogCount} OTLP logs, ignored {DuplicateLogCount} duplicates, rejected {RejectedLogCount} records.",
            accepted,
            duplicates,
            rejected);
        return new OtlpLogImportResult(accepted, duplicates, rejected);
    }

    private async Task<MonitoredComponent> GetOrCreateComponentAsync(
        string name,
        string slug,
        string environment,
        string? version,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var normalizedEnvironment = environment.Trim().ToLowerInvariant();
        var component = await db.Components.SingleOrDefaultAsync(
            x => x.Slug == slug && x.Environment == normalizedEnvironment,
            cancellationToken);

        if (component is null)
        {
            component = MonitoredComponent.Create(
                name.Trim(),
                slug,
                ComponentType.Service,
                normalizedEnvironment,
                version?.Trim(),
                now);
            db.Components.Add(component);
        }
        else
        {
            component.UpdateRegistration(name.Trim(), component.Type, version?.Trim(), now);
        }

        return component;
    }

    private static LogEventLevel MapLevel(int severityNumber) => severityNumber switch
    {
        >= 1 and <= 4 => LogEventLevel.Trace,
        >= 5 and <= 8 => LogEventLevel.Debug,
        >= 9 and <= 12 => LogEventLevel.Information,
        >= 13 and <= 16 => LogEventLevel.Warning,
        >= 17 and <= 20 => LogEventLevel.Error,
        >= 21 and <= 24 => LogEventLevel.Critical,
        _ => LogEventLevel.Unspecified
    };

    private static string BuildDedupeKey(
        Guid componentId,
        string? externalRecordId,
        string? source,
        string? traceId,
        string? spanId,
        string? eventName,
        string? severityText,
        int severityNumber,
        ulong timeUnixNano,
        ulong observedTimeUnixNano,
        string message,
        string? propertiesJson)
    {
        var identity = !string.IsNullOrWhiteSpace(externalRecordId)
            ? $"uid|{componentId:N}|{externalRecordId}"
            : string.Join('|',
                componentId.ToString("N"),
                source,
                traceId,
                spanId,
                eventName,
                severityText,
                severityNumber,
                timeUnixNano,
                observedTimeUnixNano,
                message,
                propertiesJson);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private static DateTimeOffset FromUnixNano(ulong unixNano)
    {
        if (unixNano == 0)
        {
            return DateTimeOffset.UtcNow;
        }

        var seconds = unixNano / 1_000_000_000UL;
        var remainder = unixNano % 1_000_000_000UL;
        return DateTimeOffset.FromUnixTimeSeconds(checked((long)seconds))
            .AddTicks(checked((long)(remainder / 100UL)));
    }

    private static SortedDictionary<string, object?> ToDictionary(IEnumerable<KeyValue> values)
    {
        var result = new SortedDictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in values)
        {
            if (!string.IsNullOrWhiteSpace(item.Key))
            {
                result[item.Key] = ToObject(item.Value);
            }
        }

        return result;
    }

    private static object? ToObject(AnyValue? value)
    {
        if (value is null)
        {
            return null;
        }

        return value.ValueCase switch
        {
            AnyValue.ValueOneofCase.StringValue => value.StringValue,
            AnyValue.ValueOneofCase.BoolValue => value.BoolValue,
            AnyValue.ValueOneofCase.IntValue => value.IntValue,
            AnyValue.ValueOneofCase.DoubleValue => value.DoubleValue,
            AnyValue.ValueOneofCase.BytesValue => Convert.ToBase64String(value.BytesValue.ToByteArray()),
            AnyValue.ValueOneofCase.ArrayValue => value.ArrayValue.Values.Select(ToObject).ToArray(),
            AnyValue.ValueOneofCase.KvlistValue => ToDictionary(value.KvlistValue.Values),
            _ => null
        };
    }

    private static string? GetString(IReadOnlyDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var value) ? Convert.ToString(value) : null;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();

    private static string BuildSlug(string? serviceNamespace, string serviceName)
    {
        var source = string.IsNullOrWhiteSpace(serviceNamespace) ? serviceName : $"{serviceNamespace}-{serviceName}";
        var slug = NonSlugRegex().Replace(source.ToLowerInvariant(), "-").Trim('-');
        slug = RepeatedDashRegex().Replace(slug, "-");
        if (slug.Length == 0)
        {
            slug = "unknown-service";
        }

        return slug[..Math.Min(slug.Length, 120)];
    }

    private static string ToHex(Google.Protobuf.ByteString value) =>
        Convert.ToHexString(value.ToByteArray()).ToLowerInvariant();

    private static async Task<bool> TryAcquireImportLockAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = @resource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Session',
                @LockTimeout = 15000;
            SELECT @result;
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.Value = ImportLockResource;
        command.Parameters.Add(parameter);
        var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        return result >= 0;
    }

    private static async Task ReleaseImportLockAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.Value = ImportLockResource;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [GeneratedRegex("[^a-z0-9-]+")]
    private static partial Regex NonSlugRegex();

    [GeneratedRegex("-{2,}")]
    private static partial Regex RepeatedDashRegex();
}

public sealed record OtlpLogImportResult(int AcceptedLogs, int DuplicateLogs, long RejectedLogs);

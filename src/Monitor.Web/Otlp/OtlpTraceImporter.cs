using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure;
using Monitor.Infrastructure.Failures;
using Monitor.Web.Realtime;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Trace.V1;
using OtlpSpan = OpenTelemetry.Proto.Trace.V1.Span;

namespace Monitor.Web.Otlp;

public sealed partial class OtlpTraceImporter(
    MonitorDbContext db,
    FailureGroupingService failureGrouping,
    IHubContext<MonitorHub> hub,
    ILogger<OtlpTraceImporter> logger)
{
    private const string ImportLockResource = "Monitor.OtlpTraceImport";

    public async Task<OtlpImportResult> ImportAsync(
        ExportTraceServiceRequest request,
        CancellationToken cancellationToken)
    {
        var rejected = 0L;
        var affectedRuns = new Dictionary<Guid, (AgentRun Run, MonitoredComponent Component)>();

        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            if (!await TryAcquireImportLockAsync(db.Database.GetDbConnection(), cancellationToken))
            {
                throw new TimeoutException("Monitor could not acquire the OTLP ingestion lock within 15 seconds.");
            }

            try
            {
                foreach (var resourceSpans in request.ResourceSpans)
                {
                    var resourceAttributes = ToDictionary(resourceSpans.Resource?.Attributes ?? []);
                    var serviceName = GetString(resourceAttributes, "service.name") ?? "unknown_service";
                    var serviceNamespace = GetString(resourceAttributes, "service.namespace");
                    var environment = GetString(resourceAttributes, "deployment.environment.name")
                        ?? GetString(resourceAttributes, "deployment.environment")
                        ?? "unknown";
                    var version = GetString(resourceAttributes, "service.version");
                    var slug = BuildSlug(serviceNamespace, serviceName);
                    var now = DateTimeOffset.UtcNow;

                    var allIncomingSpans = resourceSpans.ScopeSpans.SelectMany(x => x.Spans).ToList();
                    var componentType = allIncomingSpans.Any(HasAgentAttributes)
                        ? ComponentType.Agent
                        : ComponentType.Service;
                    var component = await GetOrCreateComponentAsync(
                        serviceName,
                        slug,
                        componentType,
                        environment,
                        version,
                        now,
                        cancellationToken);

                    component.Heartbeat(now);

                    foreach (var traceGroup in allIncomingSpans.GroupBy(GetTraceId))
                    {
                        if (string.IsNullOrWhiteSpace(traceGroup.Key))
                        {
                            rejected += traceGroup.LongCount();
                            continue;
                        }

                        var validIncoming = traceGroup
                            .Where(x => x.SpanId.Length == 8 && x.TraceId.Length == 16)
                            .ToList();
                        rejected += traceGroup.Count() - validIncoming.Count;
                        if (validIncoming.Count == 0)
                        {
                            continue;
                        }

                        var traceId = traceGroup.Key!;
                        var run = await db.Runs
                            .Include(x => x.Spans)
                            .SingleOrDefaultAsync(
                                x => x.ComponentId == component.Id && x.TraceId == traceId,
                                cancellationToken);

                        if (run is null)
                        {
                            var first = validIncoming.MinBy(x => x.StartTimeUnixNano)!;
                            run = AgentRun.StartOtlp(
                                component.Id,
                                traceId,
                                first.Name.Length == 0 ? "OTLP trace" : first.Name,
                                FromUnixNano(first.StartTimeUnixNano));
                            db.Runs.Add(run);
                        }

                        var knownSpanIds = run.Spans
                            .Where(x => x.ExternalSpanId != null)
                            .Select(x => x.ExternalSpanId!)
                            .ToHashSet(StringComparer.Ordinal);
                        var materializedSpans = run.Spans.ToList();

                        foreach (var incoming in validIncoming)
                        {
                            var spanId = ToHex(incoming.SpanId);
                            if (!knownSpanIds.Add(spanId))
                            {
                                continue;
                            }

                            var span = MapSpan(run.Id, incoming);
                            db.Spans.Add(span);
                            materializedSpans.Add(span);
                        }

                        ResolveParents(materializedSpans);
                        RecomputeRun(run, materializedSpans);
                        component.MarkRunStarted(now);
                        affectedRuns[run.Id] = (run, component);
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

        if (affectedRuns.Values.Any(x => x.Run.Status is RunStatus.Failed or RunStatus.Cancelled))
        {
            await failureGrouping.GroupPendingAsync(cancellationToken);
        }

        foreach (var item in affectedRuns.Values)
        {
            await hub.Clients.All.SendAsync(
                "RunChanged",
                new RunRealtimeEvent(
                    item.Run.Id,
                    item.Run.Sequence,
                    item.Run.ComponentId,
                    item.Component.Name,
                    item.Component.Environment,
                    item.Run.Name,
                    item.Run.Model,
                    item.Run.Status.ToString(),
                    item.Run.StartedAt,
                    "OTLP"),
                cancellationToken);
        }

        logger.LogDebug("Imported OTLP trace batch affecting {RunCount} runs with {RejectedSpanCount} rejected spans.", affectedRuns.Count, rejected);
        return new OtlpImportResult(affectedRuns.Count, rejected);
    }

    private async Task<MonitoredComponent> GetOrCreateComponentAsync(
        string name,
        string slug,
        ComponentType type,
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
                type,
                normalizedEnvironment,
                version?.Trim(),
                now);
            db.Components.Add(component);
        }
        else
        {
            component.UpdateRegistration(name.Trim(), type, version?.Trim(), now);
        }

        return component;
    }

    private static TraceSpan MapSpan(Guid runId, OtlpSpan span)
    {
        var attributes = ToDictionary(span.Attributes);
        foreach (var exceptionEvent in span.Events.Where(x => string.Equals(x.Name, "exception", StringComparison.OrdinalIgnoreCase)))
        {
            var eventAttributes = ToDictionary(exceptionEvent.Attributes);
            CopyIfMissing(attributes, eventAttributes, "exception.type");
            CopyIfMissing(attributes, eventAttributes, "exception.message");
            CopyIfMissing(attributes, eventAttributes, "exception.stacktrace");
        }

        var errorType = GetString(attributes, "exception.type") ?? GetString(attributes, "error.type");
        var error = span.Status?.Code == Status.Types.StatusCode.Error
            ? FirstNonEmpty(span.Status.Message, GetString(attributes, "exception.message"), GetString(attributes, "error.message"))
            : FirstNonEmpty(GetString(attributes, "exception.message"), GetString(attributes, "error.message"));
        var httpStatusCode = GetInt(attributes, "http.response.status_code") ?? GetInt(attributes, "http.status_code");
        var model = FirstNonEmpty(GetString(attributes, "gen_ai.response.model"), GetString(attributes, "gen_ai.request.model"));
        var inputTokens = GetLong(attributes, "gen_ai.usage.input_tokens") ?? 0;
        var outputTokens = GetLong(attributes, "gen_ai.usage.output_tokens") ?? 0;
        var costUsd = GetDouble(attributes, "monitor.cost_usd") ?? 0;
        var failed = span.Status?.Code == Status.Types.StatusCode.Error ||
                     !string.IsNullOrWhiteSpace(errorType) ||
                     httpStatusCode is >= 500;
        var status = failed ? SpanStatus.Failed : SpanStatus.Success;
        var startedAt = FromUnixNano(span.StartTimeUnixNano);
        var completedAt = span.EndTimeUnixNano == 0 ? startedAt : FromUnixNano(span.EndTimeUnixNano);

        return TraceSpan.CreateOtlp(
            runId,
            ToHex(span.SpanId),
            span.ParentSpanId.Length == 8 ? ToHex(span.ParentSpanId) : null,
            string.IsNullOrWhiteSpace(span.Name) ? "OTLP span" : span.Name.Trim(),
            GetSpanKind(span, attributes),
            status,
            startedAt,
            completedAt,
            JsonSerializer.Serialize(attributes),
            error,
            errorType,
            httpStatusCode,
            model,
            inputTokens,
            outputTokens,
            costUsd);
    }

    private static void ResolveParents(IReadOnlyCollection<TraceSpan> spans)
    {
        var byExternalId = spans
            .Where(x => !string.IsNullOrWhiteSpace(x.ExternalSpanId))
            .ToDictionary(x => x.ExternalSpanId!, StringComparer.Ordinal);

        foreach (var span in spans.Where(x => x.ParentSpanId == null && !string.IsNullOrWhiteSpace(x.ExternalParentSpanId)))
        {
            if (byExternalId.TryGetValue(span.ExternalParentSpanId!, out var parent))
            {
                span.ResolveParent(parent.Id);
            }
        }
    }

    private static void RecomputeRun(AgentRun run, IReadOnlyCollection<TraceSpan> spans)
    {
        if (spans.Count == 0)
        {
            return;
        }

        var model = spans.Select(x => x.Model).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        run.UpdateOtlpProvisionalStart(spans.Min(x => x.StartedAt), model);

        var root = spans
            .Where(x => x.ExternalSpanId != null && x.ExternalParentSpanId == null)
            .OrderBy(x => x.StartedAt)
            .FirstOrDefault();
        if (root?.CompletedAt is null)
        {
            return;
        }

        var failedSpan = spans.FirstOrDefault(x => x.Status == SpanStatus.Failed);
        var cancellation = failedSpan is not null && IsCancellation(failedSpan.ErrorType, failedSpan.Error);
        var status = cancellation
            ? RunStatus.Cancelled
            : failedSpan is not null || root.Status == SpanStatus.Failed
                ? RunStatus.Failed
                : RunStatus.Success;
        var error = FirstNonEmpty(root.Error, failedSpan?.Error);

        run.ApplyOtlpRoot(
            root.Name,
            status,
            root.StartedAt,
            root.CompletedAt.Value,
            FirstNonEmpty(root.Model, model),
            spans.Sum(x => x.InputTokens),
            spans.Sum(x => x.OutputTokens),
            spans.Sum(x => x.CostUsd),
            error);
    }

    private static SpanKind GetSpanKind(OtlpSpan span, IReadOnlyDictionary<string, object?> attributes)
    {
        if (attributes.Keys.Any(x => x.StartsWith("gen_ai.tool.", StringComparison.OrdinalIgnoreCase)) || attributes.ContainsKey("tool.name"))
            return SpanKind.Tool;
        if (attributes.Keys.Any(x => x.StartsWith("gen_ai.", StringComparison.OrdinalIgnoreCase)))
            return HasAgentAttributes(span) ? SpanKind.Agent : SpanKind.Model;
        if (attributes.Keys.Any(x => x.StartsWith("http.", StringComparison.OrdinalIgnoreCase)))
            return SpanKind.Http;
        return SpanKind.Internal;
    }

    private static bool HasAgentAttributes(OtlpSpan span) =>
        span.Attributes.Any(x => string.Equals(x.Key, "gen_ai.agent.name", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(x.Key, "gen_ai.agent.id", StringComparison.OrdinalIgnoreCase));

    private static bool IsCancellation(string? type, string? message)
    {
        var probe = $"{type} {message}".ToLowerInvariant();
        return probe.Contains("operationcanceled") || probe.Contains("operationcancelled") ||
               probe.Contains("taskcanceled") || probe.Contains("taskcancelled") ||
               probe.Contains("cancelled") || probe.Contains("canceled");
    }

    private static async Task<bool> TryAcquireImportLockAsync(DbConnection connection, CancellationToken cancellationToken)
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
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result) >= 0;
    }

    private static async Task ReleaseImportLockAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.Value = ImportLockResource;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? GetTraceId(OtlpSpan span) => span.TraceId.Length == 16 ? ToHex(span.TraceId) : null;
    private static string ToHex(Google.Protobuf.ByteString value) => Convert.ToHexString(value.ToByteArray()).ToLowerInvariant();

    private static DateTimeOffset FromUnixNano(ulong unixNano)
    {
        if (unixNano == 0) return DateTimeOffset.UtcNow;
        var seconds = unixNano / 1_000_000_000UL;
        var remainder = unixNano % 1_000_000_000UL;
        return DateTimeOffset.FromUnixTimeSeconds(checked((long)seconds)).AddTicks(checked((long)(remainder / 100UL)));
    }

    private static Dictionary<string, object?> ToDictionary(IEnumerable<KeyValue> values)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
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
        if (value is null) return null;
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

    private static void CopyIfMissing(Dictionary<string, object?> target, IReadOnlyDictionary<string, object?> source, string key)
    {
        if (!target.ContainsKey(key) && source.TryGetValue(key, out var value)) target[key] = value;
    }

    private static string? GetString(IReadOnlyDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var value) ? Convert.ToString(value) : null;

    private static long? GetLong(IReadOnlyDictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || value is null) return null;
        return value switch
        {
            long number => number,
            int number => number,
            _ => long.TryParse(Convert.ToString(value), out var parsed) ? parsed : null
        };
    }

    private static int? GetInt(IReadOnlyDictionary<string, object?> values, string key)
    {
        var value = GetLong(values, key);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;
    }

    private static double? GetDouble(IReadOnlyDictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || value is null) return null;
        return value switch
        {
            double number => number,
            float number => number,
            long number => number,
            _ => double.TryParse(Convert.ToString(value), System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null
        };
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();

    private static string BuildSlug(string? serviceNamespace, string serviceName)
    {
        var source = string.IsNullOrWhiteSpace(serviceNamespace) ? serviceName : $"{serviceNamespace}-{serviceName}";
        var slug = NonSlugRegex().Replace(source.ToLowerInvariant(), "-").Trim('-');
        slug = RepeatedDashRegex().Replace(slug, "-");
        if (slug.Length == 0) slug = "unknown-service";
        return slug[..Math.Min(slug.Length, 120)];
    }

    [GeneratedRegex("[^a-z0-9-]+")]
    private static partial Regex NonSlugRegex();
    [GeneratedRegex("-{2,}")]
    private static partial Regex RepeatedDashRegex();
}

public sealed record OtlpImportResult(int AffectedRuns, long RejectedSpans);

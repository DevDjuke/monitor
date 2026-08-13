using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Monitor.Infrastructure;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;

namespace Monitor.Web.Otlp;

public sealed partial class OtlpComponentScopeValidator(MonitorDbContext db)
{
    public async Task<bool> CanIngestAsync(
        ExportTraceServiceRequest request,
        Guid? authorizedComponentId,
        CancellationToken cancellationToken = default)
    {
        if (authorizedComponentId is null)
        {
            return true;
        }

        var component = await GetAuthorizedComponentAsync(authorizedComponentId.Value, cancellationToken);
        if (component is null)
        {
            return false;
        }

        foreach (var resourceSpans in request.ResourceSpans)
        {
            if (!Matches(component, resourceSpans.Resource?.Attributes ?? []))
            {
                return false;
            }
        }

        return true;
    }

    public async Task<bool> CanIngestAsync(
        ExportLogsServiceRequest request,
        Guid? authorizedComponentId,
        CancellationToken cancellationToken = default)
    {
        if (authorizedComponentId is null)
        {
            return true;
        }

        var component = await GetAuthorizedComponentAsync(authorizedComponentId.Value, cancellationToken);
        if (component is null)
        {
            return false;
        }

        foreach (var resourceLogs in request.ResourceLogs)
        {
            if (!Matches(component, resourceLogs.Resource?.Attributes ?? []))
            {
                return false;
            }
        }

        return true;
    }

    public async Task<bool> CanIngestAsync(
        ExportMetricsServiceRequest request,
        Guid? authorizedComponentId,
        CancellationToken cancellationToken = default)
    {
        if (authorizedComponentId is null)
        {
            return true;
        }

        var component = await GetAuthorizedComponentAsync(authorizedComponentId.Value, cancellationToken);
        if (component is null)
        {
            return false;
        }

        foreach (var resourceMetrics in request.ResourceMetrics)
        {
            if (!Matches(component, resourceMetrics.Resource?.Attributes ?? []))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<ComponentScope?> GetAuthorizedComponentAsync(
        Guid componentId,
        CancellationToken cancellationToken)
    {
        return await db.Components
            .AsNoTracking()
            .Where(x => x.Id == componentId && x.Enabled)
            .Select(x => new ComponentScope(x.Slug, x.Environment))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static bool Matches(ComponentScope component, IEnumerable<KeyValue> attributes)
    {
        var values = attributes.ToList();
        var serviceName = GetString(values, "service.name") ?? "unknown_service";
        var serviceNamespace = GetString(values, "service.namespace");
        var environment = GetString(values, "deployment.environment.name")
            ?? GetString(values, "deployment.environment")
            ?? "unknown";

        var slug = BuildSlug(serviceNamespace, serviceName);
        var normalizedEnvironment = environment.Trim().ToLowerInvariant();
        return string.Equals(component.Slug, slug, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(component.Environment, normalizedEnvironment, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetString(IEnumerable<KeyValue> values, string key)
    {
        var item = values.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
        return item?.Value?.ValueCase == AnyValue.ValueOneofCase.StringValue
            ? item.Value.StringValue
            : null;
    }

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

    private sealed record ComponentScope(string Slug, string Environment);

    [GeneratedRegex("[^a-z0-9-]+")]
    private static partial Regex NonSlugRegex();

    [GeneratedRegex("-{2,}")]
    private static partial Regex RepeatedDashRegex();
}

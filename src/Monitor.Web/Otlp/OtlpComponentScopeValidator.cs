using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Monitor.Infrastructure;
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

        var component = await db.Components
            .AsNoTracking()
            .Where(x => x.Id == authorizedComponentId.Value && x.Enabled)
            .Select(x => new { x.Slug, x.Environment })
            .SingleOrDefaultAsync(cancellationToken);
        if (component is null)
        {
            return false;
        }

        foreach (var resourceSpans in request.ResourceSpans)
        {
            var attributes = resourceSpans.Resource?.Attributes ?? [];
            var serviceName = GetString(attributes, "service.name") ?? "unknown_service";
            var serviceNamespace = GetString(attributes, "service.namespace");
            var environment = GetString(attributes, "deployment.environment.name")
                ?? GetString(attributes, "deployment.environment")
                ?? "unknown";

            var slug = BuildSlug(serviceNamespace, serviceName);
            var normalizedEnvironment = environment.Trim().ToLowerInvariant();
            if (!string.Equals(component.Slug, slug, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(component.Environment, normalizedEnvironment, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
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

    [GeneratedRegex("[^a-z0-9-]+")]
    private static partial Regex NonSlugRegex();

    [GeneratedRegex("-{2,}")]
    private static partial Regex RepeatedDashRegex();
}

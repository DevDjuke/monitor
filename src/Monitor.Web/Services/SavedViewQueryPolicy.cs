using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Monitor.Domain;

namespace Monitor.Web.Services;

public sealed class SavedViewQueryPolicy
{
    private static readonly IReadOnlyDictionary<SavedViewSurface, SavedViewSurfaceDefinition> Definitions =
        new Dictionary<SavedViewSurface, SavedViewSurfaceDefinition>
        {
            [SavedViewSurface.Runs] = new(
                SavedViewSurface.Runs,
                "Runs",
                "/runs",
                ["search", "componentId", "status", "environment", "model", "from", "to", "pageSize"]),
            [SavedViewSurface.Logs] = new(
                SavedViewSurface.Logs,
                "Logs",
                "/logs",
                ["Window", "Search", "ComponentId", "MinimumLevel", "Environment", "RunId", "SpanId", "Source", "Take"]),
            [SavedViewSurface.Usage] = new(
                SavedViewSurface.Usage,
                "Usage",
                "/usage",
                ["Window", "ComponentId", "Environment", "Model", "FailureCategory", "FailureSearch"]),
            [SavedViewSurface.Alerts] = new(
                SavedViewSurface.Alerts,
                "Alerts",
                "/alerts",
                ["Window", "Search", "Category", "AlertState", "RuleState", "DeliveryStatus", "DestinationId"]),
            [SavedViewSurface.Budgets] = new(
                SavedViewSurface.Budgets,
                "Budgets",
                "/budgets",
                ["Search", "State", "Period"]),
            [SavedViewSurface.Audit] = new(
                SavedViewSurface.Audit,
                "Audit",
                "/audit",
                ["Window", "ActorType", "Actor", "Action", "TargetType", "TargetId", "Search", "Take"]),
            [SavedViewSurface.Commands] = new(
                SavedViewSurface.Commands,
                "Commands",
                "/commands",
                ["Window", "ComponentId", "Type", "Status", "Search"]),
            [SavedViewSurface.Metrics] = new(
                SavedViewSurface.Metrics,
                "Metrics",
                "/metrics",
                ["Window", "Search", "ComponentId", "Environment", "Name", "Kind", "Scope", "Take"])
        };

    public SavedViewSurfaceDefinition GetDefinition(SavedViewSurface surface) =>
        Definitions.TryGetValue(surface, out var definition)
            ? definition
            : throw new ArgumentOutOfRangeException(nameof(surface), surface, "Unsupported saved-view surface.");

    public IReadOnlyList<SavedViewSurfaceDefinition> GetDefinitions() =>
        Definitions.Values.OrderBy(x => x.Surface).ToArray();

    public bool TryResolveSurface(PathString path, out SavedViewSurface surface)
    {
        var value = path.Value?.TrimEnd('/');
        if (string.IsNullOrEmpty(value))
        {
            value = "/";
        }

        foreach (var definition in Definitions.Values)
        {
            if (string.Equals(value, definition.Path, StringComparison.OrdinalIgnoreCase))
            {
                surface = definition.Surface;
                return true;
            }
        }

        surface = default;
        return false;
    }

    public string Canonicalize(SavedViewSurface surface, string? rawQueryString)
    {
        if (string.IsNullOrWhiteSpace(rawQueryString) || rawQueryString == "?")
        {
            return string.Empty;
        }

        var raw = rawQueryString.Trim();
        if (!raw.StartsWith("?", StringComparison.Ordinal))
        {
            raw = $"?{raw}";
        }

        var definition = GetDefinition(surface);
        var parsed = QueryHelpers.ParseQuery(raw);
        var values = new List<KeyValuePair<string, string?>>();

        foreach (var allowedKey in definition.QueryKeys)
        {
            var match = parsed.FirstOrDefault(x =>
                string.Equals(x.Key, allowedKey, StringComparison.OrdinalIgnoreCase));
            if (match.Key is null)
            {
                continue;
            }

            var value = match.Value.LastOrDefault()?.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (value.Length > 1024)
            {
                throw new ArgumentException($"Saved filter '{allowedKey}' is too long.", nameof(rawQueryString));
            }

            values.Add(new KeyValuePair<string, string?>(allowedKey, value));
        }

        if (values.Count == 0)
        {
            return string.Empty;
        }

        var canonical = QueryString.Create(values).Value ?? string.Empty;
        if (canonical.Length > 4000)
        {
            throw new ArgumentException("Saved view query string is too long.", nameof(rawQueryString));
        }

        return canonical;
    }

    public string BuildUrl(SavedViewSurface surface, string? queryString)
    {
        var definition = GetDefinition(surface);
        var canonical = Canonicalize(surface, queryString);
        return $"{definition.Path}{canonical}";
    }
}

public sealed record SavedViewSurfaceDefinition(
    SavedViewSurface Surface,
    string DisplayName,
    string Path,
    IReadOnlyList<string> QueryKeys);

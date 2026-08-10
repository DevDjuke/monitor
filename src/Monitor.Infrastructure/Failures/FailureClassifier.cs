using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Monitor.Domain;

namespace Monitor.Infrastructure.Failures;

public sealed partial class FailureClassifier
{
    public FailureDescriptor Classify(AgentRun run)
    {
        if (run.Status is not (RunStatus.Failed or RunStatus.Cancelled))
        {
            throw new InvalidOperationException("Only failed or cancelled runs can be classified.");
        }

        var span = run.Spans
            .Where(IsFailureCandidate)
            .OrderByDescending(x => x.Status == SpanStatus.Failed)
            .ThenBy(x => x.StartedAt)
            .FirstOrDefault();

        var attributes = ParseAttributes(span?.AttributesJson);
        var failureType = FirstNonEmpty(
            span?.ErrorType,
            GetString(attributes, "exception.type"),
            GetString(attributes, "error.type"));
        var httpStatusCode = span?.HttpStatusCode
            ?? GetInt(attributes, "http.response.status_code")
            ?? GetInt(attributes, "http.status_code");
        var dependency = FirstNonEmpty(
            GetString(attributes, "server.address"),
            GetString(attributes, "peer.service"),
            GetString(attributes, "db.system.name"),
            GetString(attributes, "gen_ai.provider.name"),
            GetString(attributes, "gen_ai.system"),
            GetString(attributes, "rpc.system"));
        var rawMessage = FirstNonEmpty(span?.Error, run.Error);
        var operation = string.IsNullOrWhiteSpace(span?.Name) ? run.Name : span.Name;
        var category = Categorize(run, span, attributes, failureType, httpStatusCode, rawMessage, dependency);
        var messageTemplate = NormalizeMessage(rawMessage);

        var canonical = string.Join('|',
            category.ToString().ToLowerInvariant(),
            NormalizeFingerprintPart(failureType),
            NormalizeFingerprintPart(operation),
            NormalizeFingerprintPart(dependency),
            httpStatusCode?.ToString() ?? string.Empty,
            NormalizeFingerprintPart(messageTemplate));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

        return new FailureDescriptor(
            fingerprint,
            category,
            TrimTo(failureType, 240),
            TrimTo(operation, 240) ?? "unknown",
            TrimTo(dependency, 240),
            httpStatusCode,
            TrimTo(messageTemplate, 500));
    }

    private static bool IsFailureCandidate(TraceSpan span) =>
        span.Status == SpanStatus.Failed ||
        !string.IsNullOrWhiteSpace(span.Error) ||
        !string.IsNullOrWhiteSpace(span.ErrorType) ||
        span.HttpStatusCode is >= 400;

    private static FailureCategory Categorize(
        AgentRun run,
        TraceSpan? span,
        IReadOnlyDictionary<string, JsonElement> attributes,
        string? failureType,
        int? httpStatusCode,
        string? message,
        string? dependency)
    {
        var probe = $"{failureType} {message}".ToLowerInvariant();

        if (run.Status == RunStatus.Cancelled || ContainsAny(probe, "operationcanceled", "operationcancelled", "taskcanceled", "taskcancelled", "cancelled", "canceled"))
            return FailureCategory.Cancellation;
        if (ContainsAny(probe, "timeout", "timed out", "deadlineexceeded"))
            return FailureCategory.Timeout;
        if (httpStatusCode == 429 || ContainsAny(probe, "rate limit", "ratelimit", "throttl", "too many requests"))
            return FailureCategory.RateLimit;
        if (httpStatusCode == 401 || ContainsAny(probe, "unauthenticated", "authentication", "invalid api key", "invalid token"))
            return FailureCategory.Authentication;
        if (httpStatusCode == 403 || ContainsAny(probe, "unauthorized", "authorization", "permission denied", "forbidden"))
            return FailureCategory.Authorization;
        if (ContainsAny(probe, "validation", "argumentexception", "argumentoutofrange", "invalid argument"))
            return FailureCategory.Validation;
        if (ContainsAny(probe, "jsonexception", "serialization", "deserializ", "parseexception", "formatexception"))
            return FailureCategory.Serialization;
        if (attributes.Keys.Any(x => x.StartsWith("db.", StringComparison.OrdinalIgnoreCase)) || ContainsAny(probe, "sqlexception", "dbexception", "database"))
            return FailureCategory.Database;
        if (span?.Kind == SpanKind.Model || attributes.Keys.Any(x => x.StartsWith("gen_ai.", StringComparison.OrdinalIgnoreCase)))
            return FailureCategory.ModelProvider;
        if (span?.Kind == SpanKind.Tool)
            return FailureCategory.Tool;
        if (httpStatusCode is >= 400 || span?.Kind == SpanKind.Http)
            return FailureCategory.Http;
        if (ContainsAny(probe, "socketexception", "httprequestexception", "dns", "connection refused", "connection reset", "network"))
            return FailureCategory.Network;
        if (!string.IsNullOrWhiteSpace(dependency))
            return FailureCategory.Dependency;
        if (!string.IsNullOrWhiteSpace(failureType) || !string.IsNullOrWhiteSpace(message))
            return FailureCategory.Internal;
        return FailureCategory.Unknown;
    }

    private static IReadOnlyDictionary<string, JsonElement> ParseAttributes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

            return document.RootElement.EnumerateObject()
                .ToDictionary(x => x.Name, x => x.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string? GetString(IReadOnlyDictionary<string, JsonElement> attributes, string key)
    {
        if (!attributes.TryGetValue(key, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static int? GetInt(IReadOnlyDictionary<string, JsonElement> attributes, string key)
    {
        if (!attributes.TryGetValue(key, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return int.TryParse(value.ToString(), out number) ? number : null;
    }

    private static string? NormalizeMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var firstLine = message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(firstLine)) return null;

        var normalized = UrlRegex().Replace(firstLine, "<url>");
        normalized = GuidRegex().Replace(normalized, "<guid>");
        normalized = HexRegex().Replace(normalized, "<hex>");
        normalized = QuotedRegex().Replace(normalized, "\"<value>\"");
        normalized = NumberRegex().Replace(normalized, "<n>");
        normalized = WhitespaceRegex().Replace(normalized, " ").Trim();
        return normalized;
    }

    private static string NormalizeFingerprintPart(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(value.Contains);

    private static string? TrimTo(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, maxLength)];

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();
    [GeneratedRegex(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\b", RegexOptions.IgnoreCase)]
    private static partial Regex GuidRegex();
    [GeneratedRegex(@"\b(?:0x)?[0-9a-f]{12,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex HexRegex();
    [GeneratedRegex("[\"']([^\"']{2,})[\"']")]
    private static partial Regex QuotedRegex();
    [GeneratedRegex(@"\b\d+(?:\.\d+)?\b")]
    private static partial Regex NumberRegex();
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

public sealed record FailureDescriptor(
    string Fingerprint,
    FailureCategory Category,
    string? FailureType,
    string Operation,
    string? Dependency,
    int? HttpStatusCode,
    string? MessageTemplate);

using System.Text.Json;
using Monitor.Domain;

namespace Monitor.Infrastructure.Failures;

public static class FailureAlertPayloadSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static string Serialize(
        FailureAlertEvent alertEvent,
        FailureAlertRule rule,
        FailureGroup failureGroup)
    {
        var payload = new
        {
            schemaVersion = 1,
            eventType = "failure.alert.triggered",
            alert = new
            {
                id = alertEvent.Id,
                triggeredAt = alertEvent.TriggeredAt,
                rule = new
                {
                    id = rule.Id,
                    name = rule.Name,
                    threshold = rule.Threshold,
                    windowMinutes = rule.WindowMinutes,
                    cooldownMinutes = rule.CooldownMinutes
                },
                failureGroup = new
                {
                    id = failureGroup.Id,
                    fingerprint = failureGroup.Fingerprint,
                    category = failureGroup.Category.ToString(),
                    failureType = failureGroup.FailureType,
                    operation = failureGroup.Operation,
                    dependency = failureGroup.Dependency,
                    httpStatusCode = failureGroup.HttpStatusCode,
                    messageTemplate = failureGroup.MessageTemplate,
                    occurrences = failureGroup.Occurrences,
                    firstSeenAt = failureGroup.FirstSeenAt,
                    lastSeenAt = failureGroup.LastSeenAt
                },
                window = new
                {
                    start = alertEvent.WindowStart,
                    end = alertEvent.WindowEnd,
                    occurrences = alertEvent.OccurrencesInWindow,
                    threshold = alertEvent.Threshold,
                    latestRunSequence = alertEvent.LatestRunSequence
                }
            }
        };

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }
}

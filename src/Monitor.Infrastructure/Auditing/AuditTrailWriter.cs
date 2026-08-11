using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Monitor.Domain;

namespace Monitor.Infrastructure.Auditing;

public sealed class AuditTrailWriter(MonitorDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public AuditEvent RecordOperator(
        ClaimsPrincipal user,
        string action,
        string targetType,
        string? targetId,
        string? targetName = null,
        object? before = null,
        object? after = null,
        object? metadata = null,
        DateTimeOffset? occurredAt = null)
    {
        ArgumentNullException.ThrowIfNull(user);

        return Record(
            AuditActorType.Operator,
            user.FindFirstValue(ClaimTypes.NameIdentifier),
            user.Identity?.Name,
            action,
            targetType,
            targetId,
            targetName,
            before,
            after,
            metadata,
            occurredAt ?? DateTimeOffset.UtcNow);
    }

    public AuditEvent RecordSystem(
        string actorName,
        string action,
        string targetType,
        string? targetId,
        string? targetName = null,
        object? before = null,
        object? after = null,
        object? metadata = null,
        DateTimeOffset? occurredAt = null)
    {
        return Record(
            AuditActorType.System,
            actorId: null,
            actorName,
            action,
            targetType,
            targetId,
            targetName,
            before,
            after,
            metadata,
            occurredAt ?? DateTimeOffset.UtcNow);
    }

    public AuditEvent RecordComponent(
        Guid componentId,
        string componentName,
        string action,
        string targetType,
        string? targetId,
        string? targetName = null,
        object? before = null,
        object? after = null,
        object? metadata = null,
        DateTimeOffset? occurredAt = null)
    {
        return Record(
            AuditActorType.Component,
            componentId.ToString("D"),
            componentName,
            action,
            targetType,
            targetId,
            targetName,
            before,
            after,
            metadata,
            occurredAt ?? DateTimeOffset.UtcNow);
    }

    private AuditEvent Record(
        AuditActorType actorType,
        string? actorId,
        string? actorName,
        string action,
        string targetType,
        string? targetId,
        string? targetName,
        object? before,
        object? after,
        object? metadata,
        DateTimeOffset occurredAt)
    {
        var auditEvent = AuditEvent.Create(
            actorType,
            actorId,
            actorName,
            action,
            targetType,
            targetId,
            targetName,
            Serialize(before),
            Serialize(after),
            Serialize(metadata),
            occurredAt);

        db.AuditEvents.Add(auditEvent);
        return auditEvent;
    }

    private static string? Serialize(object? value) =>
        value is null ? null : JsonSerializer.Serialize(value, JsonOptions);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

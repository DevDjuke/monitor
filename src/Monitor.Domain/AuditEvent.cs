namespace Monitor.Domain;

public enum AuditActorType
{
    Operator,
    System,
    Component
}

public sealed class AuditEvent
{
    private AuditEvent() { }

    public Guid Id { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public AuditActorType ActorType { get; private set; }
    public string? ActorId { get; private set; }
    public string? ActorName { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string TargetType { get; private set; } = string.Empty;
    public string? TargetId { get; private set; }
    public string? TargetName { get; private set; }
    public string? BeforeJson { get; private set; }
    public string? AfterJson { get; private set; }
    public string? MetadataJson { get; private set; }

    public static AuditEvent Create(
        AuditActorType actorType,
        string? actorId,
        string? actorName,
        string action,
        string targetType,
        string? targetId,
        string? targetName,
        string? beforeJson,
        string? afterJson,
        string? metadataJson,
        DateTimeOffset occurredAt)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            throw new ArgumentException("Audit action is required.", nameof(action));
        }

        if (string.IsNullOrWhiteSpace(targetType))
        {
            throw new ArgumentException("Audit target type is required.", nameof(targetType));
        }

        return new AuditEvent
        {
            Id = Guid.NewGuid(),
            OccurredAt = occurredAt,
            ActorType = actorType,
            ActorId = Normalize(actorId),
            ActorName = Normalize(actorName),
            Action = action.Trim(),
            TargetType = targetType.Trim(),
            TargetId = Normalize(targetId),
            TargetName = Normalize(targetName),
            BeforeJson = Normalize(beforeJson),
            AfterJson = Normalize(afterJson),
            MetadataJson = Normalize(metadataJson)
        };
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public static class AuditActions
{
    public const string AlertAcknowledged = "alert.acknowledged";

    public const string AlertRuleCreated = "alert-rule.created";
    public const string AlertRuleUpdated = "alert-rule.updated";
    public const string AlertRuleDeleted = "alert-rule.deleted";
    public const string AlertRuleEnabled = "alert-rule.enabled";
    public const string AlertRuleDisabled = "alert-rule.disabled";

    public const string AlertDestinationCreated = "alert-destination.created";
    public const string AlertDestinationEnabled = "alert-destination.enabled";
    public const string AlertDestinationDisabled = "alert-destination.disabled";
    public const string AlertDestinationTested = "alert-destination.tested";
    public const string AlertDeliveryRequeued = "alert-delivery.requeued";

    public const string ComponentCredentialIssued = "component-credential.issued";
    public const string ComponentCredentialRotated = "component-credential.rotated";
    public const string ComponentCredentialRevoked = "component-credential.revoked";

    public const string ComponentCommandIssued = "component-command.issued";
    public const string ComponentCommandCancelled = "component-command.cancelled";
    public const string ComponentCommandSucceeded = "component-command.succeeded";
    public const string ComponentCommandFailed = "component-command.failed";
    public const string ComponentCommandRejected = "component-command.rejected";
    public const string ComponentCommandExpired = "component-command.expired";

    public const string OperatorAccountCreated = "operator-account.created";
    public const string OperatorRoleChanged = "operator-account.role-changed";
    public const string OperatorPasswordReset = "operator-account.password-reset";
    public const string OperatorAccountDeleted = "operator-account.deleted";
}

public static class AuditTargetTypes
{
    public const string Alert = "alert";
    public const string AlertRule = "alert-rule";
    public const string AlertDestination = "alert-destination";
    public const string AlertDelivery = "alert-delivery";
    public const string ComponentCredential = "component-credential";
    public const string ComponentCommand = "component-command";
    public const string OperatorAccount = "operator-account";
}

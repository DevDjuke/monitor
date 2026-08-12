namespace Monitor.Domain;

public enum AlertDeliveryKind
{
    Webhook = 1,
    Slack = 2,
    MicrosoftTeams = 3,
    Discord = 4,
    PagerDuty = 5,
    Email = 6
}

public sealed class AlertDeliveryDestination
{
    private AlertDeliveryDestination() { }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public AlertDeliveryKind Kind { get; private set; }
    public string EndpointUrl { get; private set; } = string.Empty;
    public string ProtectedSecret { get; private set; } = string.Empty;
    public bool Enabled { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? LastSuccessAt { get; private set; }
    public DateTimeOffset? LastFailureAt { get; private set; }
    public string? LastFailure { get; private set; }

    public ICollection<AlertDelivery> Deliveries { get; private set; } = new List<AlertDelivery>();
    public ICollection<FailureAlertRuleDestination> AlertRuleAssignments { get; private set; } = new List<FailureAlertRuleDestination>();

    public static AlertDeliveryDestination CreateWebhook(
        string name,
        string endpointUrl,
        string protectedSecret,
        DateTimeOffset now) =>
        Create(name, AlertDeliveryKind.Webhook, endpointUrl, protectedSecret, now);

    public static AlertDeliveryDestination CreateAdapter(
        string name,
        AlertDeliveryKind kind,
        string endpointDisplay,
        string protectedSecret,
        DateTimeOffset now)
    {
        if (kind == AlertDeliveryKind.Webhook)
        {
            throw new ArgumentException("Use CreateWebhook for signed webhook destinations.", nameof(kind));
        }

        return Create(name, kind, endpointDisplay, protectedSecret, now);
    }

    public void SetEnabled(bool enabled, DateTimeOffset now)
    {
        Enabled = enabled;
        UpdatedAt = now;
    }

    public void RecordSuccess(DateTimeOffset now)
    {
        LastSuccessAt = now;
        LastFailure = null;
        UpdatedAt = now;
    }

    public void RecordFailure(string error, DateTimeOffset now)
    {
        LastFailureAt = now;
        LastFailure = string.IsNullOrWhiteSpace(error) ? "Delivery failed." : error.Trim();
        UpdatedAt = now;
    }

    private static AlertDeliveryDestination Create(
        string name,
        AlertDeliveryKind kind,
        string endpointDisplay,
        string protectedSecret,
        DateTimeOffset now)
    {
        Validate(name, kind, endpointDisplay, protectedSecret);

        return new AlertDeliveryDestination
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Kind = kind,
            EndpointUrl = endpointDisplay.Trim(),
            ProtectedSecret = protectedSecret,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static void Validate(
        string name,
        AlertDeliveryKind kind,
        string endpointDisplay,
        string protectedSecret)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Destination name is required.", nameof(name));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (string.IsNullOrWhiteSpace(endpointDisplay))
        {
            throw new ArgumentException("Destination endpoint is required.", nameof(endpointDisplay));
        }

        if (kind == AlertDeliveryKind.Email)
        {
            if (!Uri.TryCreate(endpointDisplay, UriKind.Absolute, out var emailUri) ||
                !string.Equals(emailUri.Scheme, Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Email destinations must use a mailto endpoint.", nameof(endpointDisplay));
            }
        }
        else if (!Uri.TryCreate(endpointDisplay, UriKind.Absolute, out var uri) ||
                 (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Delivery endpoint must be an absolute HTTP or HTTPS URL.", nameof(endpointDisplay));
        }

        if (string.IsNullOrWhiteSpace(protectedSecret))
        {
            throw new ArgumentException("Protected destination configuration is required.", nameof(protectedSecret));
        }
    }
}

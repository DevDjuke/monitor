namespace Monitor.Domain;

public enum AlertDeliveryKind
{
    Webhook = 1
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
        DateTimeOffset now)
    {
        Validate(name, endpointUrl, protectedSecret);

        return new AlertDeliveryDestination
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Kind = AlertDeliveryKind.Webhook,
            EndpointUrl = endpointUrl.Trim(),
            ProtectedSecret = protectedSecret,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now
        };
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

    private static void Validate(string name, string endpointUrl, string protectedSecret)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Destination name is required.", nameof(name));
        }

        if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Webhook URL must be an absolute HTTP or HTTPS URL.", nameof(endpointUrl));
        }

        if (string.IsNullOrWhiteSpace(protectedSecret))
        {
            throw new ArgumentException("A protected webhook secret is required.", nameof(protectedSecret));
        }
    }
}

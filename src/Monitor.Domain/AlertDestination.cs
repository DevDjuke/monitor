namespace Monitor.Domain;

public enum AlertDestinationKind
{
    Webhook = 1
}

public sealed class AlertDestination
{
    private AlertDestination() { }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public AlertDestinationKind Kind { get; private set; }
    public string Endpoint { get; private set; } = string.Empty;
    public string ProtectedSigningSecret { get; private set; } = string.Empty;
    public bool Enabled { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public ICollection<FailureAlertRoute> AlertRoutes { get; private set; } = new List<FailureAlertRoute>();
    public ICollection<AlertDelivery> Deliveries { get; private set; } = new List<AlertDelivery>();

    public static AlertDestination CreateWebhook(
        string name,
        string endpoint,
        string protectedSigningSecret,
        DateTimeOffset now)
    {
        return new AlertDestination
        {
            Id = Guid.NewGuid(),
            Name = NormalizeName(name),
            Kind = AlertDestinationKind.Webhook,
            Endpoint = NormalizeEndpoint(endpoint),
            ProtectedSigningSecret = NormalizeProtectedSecret(protectedSigningSecret),
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

    public void UpdateWebhook(string name, string endpoint, DateTimeOffset now)
    {
        Name = NormalizeName(name);
        Endpoint = NormalizeEndpoint(endpoint);
        UpdatedAt = now;
    }

    public void RotateSigningSecret(string protectedSigningSecret, DateTimeOffset now)
    {
        ProtectedSigningSecret = NormalizeProtectedSecret(protectedSigningSecret);
        UpdatedAt = now;
    }

    private static string NormalizeName(string name)
    {
        var normalized = name?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Destination name is required.", nameof(name));
        }

        if (normalized.Length > 200)
        {
            throw new ArgumentException("Destination name cannot exceed 200 characters.", nameof(name));
        }

        return normalized;
    }

    private static string NormalizeEndpoint(string endpoint)
    {
        var normalized = endpoint?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) ||
            !Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("Webhook endpoint must be an absolute HTTP or HTTPS URL.", nameof(endpoint));
        }

        if (normalized.Length > 2048)
        {
            throw new ArgumentException("Webhook endpoint cannot exceed 2048 characters.", nameof(endpoint));
        }

        return uri.AbsoluteUri;
    }

    private static string NormalizeProtectedSecret(string protectedSigningSecret)
    {
        var normalized = protectedSigningSecret?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Protected signing secret is required.", nameof(protectedSigningSecret));
        }

        return normalized;
    }
}

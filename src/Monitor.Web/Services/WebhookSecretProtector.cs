using Microsoft.AspNetCore.DataProtection;

namespace Monitor.Web.Services;

public sealed class WebhookSecretProtector(IDataProtectionProvider dataProtectionProvider)
{
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(
        "Monitor.AlertDelivery.WebhookSigningSecret.v1");

    public string Protect(string signingSecret)
    {
        var normalized = Normalize(signingSecret);
        return _protector.Protect(normalized);
    }

    public string Unprotect(string protectedSigningSecret)
    {
        if (string.IsNullOrWhiteSpace(protectedSigningSecret))
        {
            throw new InvalidOperationException("Webhook signing secret is missing.");
        }

        return _protector.Unprotect(protectedSigningSecret);
    }

    private static string Normalize(string signingSecret)
    {
        var normalized = signingSecret?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Webhook signing secret is required.", nameof(signingSecret));
        }

        if (normalized.Length < 16)
        {
            throw new ArgumentException("Webhook signing secret must contain at least 16 characters.", nameof(signingSecret));
        }

        if (normalized.Length > 512)
        {
            throw new ArgumentException("Webhook signing secret cannot exceed 512 characters.", nameof(signingSecret));
        }

        return normalized;
    }
}

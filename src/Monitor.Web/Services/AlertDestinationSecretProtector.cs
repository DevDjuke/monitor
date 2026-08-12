using Microsoft.AspNetCore.DataProtection;

namespace Monitor.Web.Services;

public sealed class AlertDestinationSecretProtector(IDataProtectionProvider dataProtectionProvider)
{
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("Monitor.AlertDelivery.AdapterSecret.v1");

    public string Protect(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Destination secret/configuration is required.", nameof(value));
        }

        return _protector.Protect(value.Trim());
    }

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}

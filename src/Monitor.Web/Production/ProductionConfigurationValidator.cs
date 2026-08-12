using System.Net;

namespace Monitor.Web.Production;

public static class ProductionConfigurationValidator
{
    public static ProductionOptions BindAndValidate(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        string connectionString,
        bool migrateOnly)
    {
        var options = new ProductionOptions();
        configuration.GetSection(ProductionOptions.SectionName).Bind(options);
        options.DataProtectionApplicationName = options.DataProtectionApplicationName?.Trim() ?? string.Empty;
        options.DataProtectionKeyPath = options.DataProtectionKeyPath?.Trim() ?? string.Empty;
        options.PublicUrl = options.PublicUrl?.Trim() ?? string.Empty;
        options.ForwardedHeaders ??= new ForwardedHeaderTrustOptions();
        options.ForwardedHeaders.KnownProxies ??= new List<string>();
        options.ForwardedHeaders.KnownProxies = options.ForwardedHeaders.KnownProxies
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!environment.IsProduction())
        {
            return options;
        }

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            errors.Add("ConnectionStrings:Monitor is required in Production.");
        }
        else if (connectionString.Contains("(localdb)", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("ConnectionStrings:Monitor cannot use LocalDB in Production.");
        }

        if (!migrateOnly)
        {
            ValidateHttpConfiguration(configuration, options, errors);
            ValidateDataProtection(options, errors);
            ValidateBootstrapPair(configuration, errors);
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Monitor production configuration is invalid:" + Environment.NewLine +
                string.Join(Environment.NewLine, errors.Select(x => $" - {x}")));
        }

        if (!migrateOnly)
        {
            EnsureWritableDirectory(options.DataProtectionKeyPath);
        }

        return options;
    }

    private static void ValidateHttpConfiguration(
        IConfiguration configuration,
        ProductionOptions options,
        ICollection<string> errors)
    {
        var allowedHosts = configuration["AllowedHosts"];
        if (string.IsNullOrWhiteSpace(allowedHosts) ||
            allowedHosts.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(x => x == "*"))
        {
            errors.Add("AllowedHosts must explicitly list the public Monitor host in Production; wildcard '*' is not allowed.");
        }

        if (!Uri.TryCreate(options.PublicUrl, UriKind.Absolute, out var publicUri) ||
            !string.Equals(publicUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(publicUri.Host))
        {
            errors.Add("Production:PublicUrl must be an absolute HTTPS URL.");
        }

        if (options.ForwardedHeaders.Enabled)
        {
            if (options.ForwardedHeaders.KnownProxies.Count == 0)
            {
                errors.Add("Production:ForwardedHeaders:KnownProxies must contain at least one explicit proxy IP when forwarded headers are enabled.");
            }

            foreach (var proxy in options.ForwardedHeaders.KnownProxies)
            {
                if (!IPAddress.TryParse(proxy, out _))
                {
                    errors.Add($"Production:ForwardedHeaders:KnownProxies contains invalid IP address '{proxy}'.");
                }
            }
        }
    }

    private static void ValidateDataProtection(ProductionOptions options, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(options.DataProtectionApplicationName))
        {
            errors.Add("Production:DataProtectionApplicationName is required.");
        }

        if (string.IsNullOrWhiteSpace(options.DataProtectionKeyPath))
        {
            errors.Add("Production:DataProtectionKeyPath is required so protected configuration survives restarts.");
        }
        else if (!Path.IsPathRooted(options.DataProtectionKeyPath))
        {
            errors.Add("Production:DataProtectionKeyPath must be an absolute path.");
        }
    }

    private static void ValidateBootstrapPair(IConfiguration configuration, ICollection<string> errors)
    {
        var email = configuration["Monitor:BootstrapAdmin:Email"];
        var password = configuration["Monitor:BootstrapAdmin:Password"];
        if (string.IsNullOrWhiteSpace(email) != string.IsNullOrWhiteSpace(password))
        {
            errors.Add("Monitor:BootstrapAdmin:Email and Monitor:BootstrapAdmin:Password must be configured together.");
        }
    }

    private static void EnsureWritableDirectory(string path)
    {
        Directory.CreateDirectory(path);
        var probe = Path.Combine(path, $".monitor-write-probe-{Guid.NewGuid():N}");

        try
        {
            File.WriteAllText(probe, "ok");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Production:DataProtectionKeyPath '{path}' is not writable by the Monitor process.", ex);
        }
        finally
        {
            try
            {
                if (File.Exists(probe))
                {
                    File.Delete(probe);
                }
            }
            catch
            {
                // The probe proved the directory writable; cleanup failure is non-fatal.
            }
        }
    }
}

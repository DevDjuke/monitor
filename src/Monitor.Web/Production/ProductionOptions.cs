namespace Monitor.Web.Production;

public sealed class ProductionOptions
{
    public const string SectionName = "Production";

    public bool MigrateOnStartup { get; set; } = true;

    public string DataProtectionKeyPath { get; set; } = string.Empty;

    public string DataProtectionApplicationName { get; set; } = "Monitor";

    public string PublicUrl { get; set; } = string.Empty;

    public bool UseHttpsRedirection { get; set; } = true;

    public ForwardedHeaderTrustOptions ForwardedHeaders { get; set; } = new();
}

public sealed class ForwardedHeaderTrustOptions
{
    public bool Enabled { get; set; }

    public List<string> KnownProxies { get; set; } = new();
}

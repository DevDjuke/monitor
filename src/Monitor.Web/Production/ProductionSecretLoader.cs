namespace Monitor.Web.Production;

public static class ProductionSecretLoader
{
    private const string DefaultSecretsPath = "/run/secrets";
    private const int MaxSecretBytes = 64 * 1024;

    public static void Load(ConfigurationManager configuration)
    {
        var path = Environment.GetEnvironmentVariable("MONITOR_SECRETS_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            path = DefaultSecretsPath;
        }

        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.') || !name.Contains("__", StringComparison.Ordinal))
            {
                continue;
            }

            var info = new FileInfo(file);
            if (info.Length > MaxSecretBytes)
            {
                throw new InvalidOperationException($"Secret file '{name}' exceeds the {MaxSecretBytes}-byte limit.");
            }

            var key = name.Replace("__", ":", StringComparison.Ordinal);
            configuration[key] = File.ReadAllText(file).TrimEnd('\r', '\n');
        }
    }
}

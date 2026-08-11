namespace Monitor.Domain;

public enum SavedViewSurface
{
    Runs = 1,
    Logs = 2,
    Usage = 3,
    Alerts = 4,
    Budgets = 5,
    Audit = 6,
    Commands = 7
}

public sealed class SavedView
{
    private SavedView() { }

    public Guid Id { get; private set; }
    public string UserId { get; private set; } = string.Empty;
    public SavedViewSurface Surface { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string NameKey { get; private set; } = string.Empty;
    public string QueryString { get; private set; } = string.Empty;
    public bool IsPinned { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static SavedView Create(
        string userId,
        SavedViewSurface surface,
        string name,
        string queryString,
        bool isPinned,
        DateTimeOffset now)
    {
        var normalizedUserId = NormalizeRequired(userId, nameof(userId), 450);
        var normalizedName = NormalizeRequired(name, nameof(name), 120);
        var normalizedQuery = NormalizeQueryString(queryString);

        return new SavedView
        {
            Id = Guid.NewGuid(),
            UserId = normalizedUserId,
            Surface = surface,
            Name = normalizedName,
            NameKey = NormalizeNameKey(normalizedName),
            QueryString = normalizedQuery,
            IsPinned = isPinned,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public void Rename(string name, DateTimeOffset now)
    {
        var normalizedName = NormalizeRequired(name, nameof(name), 120);
        Name = normalizedName;
        NameKey = NormalizeNameKey(normalizedName);
        UpdatedAt = now;
    }

    public void ReplaceQueryString(string queryString, DateTimeOffset now)
    {
        QueryString = NormalizeQueryString(queryString);
        UpdatedAt = now;
    }

    public void SetPinned(bool isPinned, DateTimeOffset now)
    {
        if (IsPinned == isPinned)
        {
            return;
        }

        IsPinned = isPinned;
        UpdatedAt = now;
    }

    private static string NormalizeQueryString(string value)
    {
        value ??= string.Empty;
        value = value.Trim();
        if (value.Length > 4000)
        {
            throw new ArgumentException("Saved view query string is too long.", nameof(value));
        }

        if (value.Length > 0 && value[0] != '?')
        {
            throw new ArgumentException("Saved view query string must be canonical and begin with '?'.", nameof(value));
        }

        return value;
    }

    private static string NormalizeRequired(string value, string parameterName, int maxLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("A value is required.", parameterName);
        }

        if (normalized.Length > maxLength)
        {
            throw new ArgumentException($"Value cannot exceed {maxLength} characters.", parameterName);
        }

        return normalized;
    }

    private static string NormalizeNameKey(string name) => name.ToUpperInvariant();
}

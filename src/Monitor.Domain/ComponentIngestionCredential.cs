namespace Monitor.Domain;

public sealed class ComponentIngestionCredential
{
    private ComponentIngestionCredential() { }

    public Guid Id { get; private set; }
    public Guid ComponentId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string KeyId { get; private set; } = string.Empty;
    public byte[] KeyHash { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; }
    public string? CreatedBy { get; private set; }
    public DateTimeOffset? LastUsedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? RevokedBy { get; private set; }

    public MonitoredComponent Component { get; private set; } = null!;

    public bool IsRevoked => RevokedAt is not null;

    public static ComponentIngestionCredential Create(
        Guid componentId,
        string name,
        string keyId,
        byte[] keyHash,
        string? createdBy,
        DateTimeOffset now)
    {
        if (componentId == Guid.Empty)
        {
            throw new ArgumentException("Component id is required.", nameof(componentId));
        }

        var normalizedName = name?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new ArgumentException("Credential name is required.", nameof(name));
        }

        var normalizedKeyId = keyId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedKeyId))
        {
            throw new ArgumentException("Credential key id is required.", nameof(keyId));
        }

        if (keyHash is not { Length: 32 })
        {
            throw new ArgumentException("Credential hash must be a SHA-256 hash.", nameof(keyHash));
        }

        return new ComponentIngestionCredential
        {
            Id = Guid.NewGuid(),
            ComponentId = componentId,
            Name = normalizedName,
            KeyId = normalizedKeyId,
            KeyHash = keyHash.ToArray(),
            CreatedAt = now,
            CreatedBy = NormalizeActor(createdBy)
        };
    }

    public void Revoke(string? revokedBy, DateTimeOffset now)
    {
        if (RevokedAt is not null)
        {
            return;
        }

        RevokedAt = now;
        RevokedBy = NormalizeActor(revokedBy);
    }

    private static string? NormalizeActor(string? actor)
    {
        var value = actor?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

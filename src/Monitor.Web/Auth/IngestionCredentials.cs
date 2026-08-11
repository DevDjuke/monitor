using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure;

namespace Monitor.Web.Auth;

public enum IngestionIdentityKind
{
    Operator,
    BootstrapKey,
    ComponentCredential
}

public sealed record IngestionIdentity(
    IngestionIdentityKind Kind,
    Guid? ComponentId = null,
    Guid? CredentialId = null)
{
    public bool IsPrivileged => Kind is IngestionIdentityKind.Operator or IngestionIdentityKind.BootstrapKey;

    public bool CanAccess(Guid componentId) =>
        IsPrivileged || ComponentId == componentId;
}

public sealed record IssuedComponentCredential(
    ComponentIngestionCredential Credential,
    string PlaintextKey);

public sealed class ComponentCredentialIssuer(MonitorDbContext db)
{
    private const string Prefix = "mon_c_";

    public async Task<IssuedComponentCredential> IssueAsync(
        Guid componentId,
        string name,
        string? createdBy,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!await db.Components.AnyAsync(x => x.Id == componentId, cancellationToken))
        {
            throw new InvalidOperationException("The component does not exist.");
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var keyId = ToBase64Url(RandomNumberGenerator.GetBytes(12));
            if (await db.ComponentIngestionCredentials.AnyAsync(x => x.KeyId == keyId, cancellationToken))
            {
                continue;
            }

            var secret = ToBase64Url(RandomNumberGenerator.GetBytes(32));
            var plaintext = $"{Prefix}{keyId}.{secret}";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));
            var credential = ComponentIngestionCredential.Create(
                componentId,
                name,
                keyId,
                hash,
                createdBy,
                now);

            db.ComponentIngestionCredentials.Add(credential);
            return new IssuedComponentCredential(credential, plaintext);
        }

        throw new InvalidOperationException("Could not allocate a unique component credential id.");
    }

    private static string ToBase64Url(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

public sealed class IngestionCredentialAuthenticator(
    MonitorDbContext db,
    IConfiguration configuration)
{
    private const string Prefix = "mon_c_";
    private static readonly object IdentityItemKey = new();

    public async Task<IngestionIdentity?> AuthenticateAsync(
        HttpContext httpContext,
        bool allowOperator,
        CancellationToken cancellationToken = default)
    {
        if (allowOperator && httpContext.User.Identity?.IsAuthenticated == true)
        {
            return new IngestionIdentity(IngestionIdentityKind.Operator);
        }

        var suppliedKey = httpContext.Request.Headers["X-Monitor-Key"].ToString();
        if (string.IsNullOrWhiteSpace(suppliedKey))
        {
            return null;
        }

        var bootstrapKey = configuration["Monitor:IngestionApiKey"];
        if (!string.IsNullOrWhiteSpace(bootstrapKey) && KeysMatch(bootstrapKey, suppliedKey))
        {
            return new IngestionIdentity(IngestionIdentityKind.BootstrapKey);
        }

        if (!TryGetKeyId(suppliedKey, out var keyId))
        {
            return null;
        }

        var credential = await db.ComponentIngestionCredentials
            .AsNoTracking()
            .Where(x =>
                x.KeyId == keyId &&
                x.RevokedAt == null &&
                x.Component.Enabled)
            .Select(x => new CredentialLookup(
                x.Id,
                x.ComponentId,
                x.KeyHash,
                x.LastUsedAt))
            .SingleOrDefaultAsync(cancellationToken);

        if (credential is null)
        {
            return null;
        }

        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedKey));
        if (credential.KeyHash.Length != suppliedHash.Length ||
            !CryptographicOperations.FixedTimeEquals(credential.KeyHash, suppliedHash))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var writeCutoff = now.AddMinutes(-1);
        if (credential.LastUsedAt is null || credential.LastUsedAt < writeCutoff)
        {
            await db.ComponentIngestionCredentials
                .Where(x =>
                    x.Id == credential.Id &&
                    x.RevokedAt == null &&
                    (x.LastUsedAt == null || x.LastUsedAt < writeCutoff))
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(x => x.LastUsedAt, now),
                    cancellationToken);
        }

        return new IngestionIdentity(
            IngestionIdentityKind.ComponentCredential,
            credential.ComponentId,
            credential.Id);
    }

    public static void SetIdentity(HttpContext httpContext, IngestionIdentity identity) =>
        httpContext.Items[IdentityItemKey] = identity;

    public static IngestionIdentity GetIdentity(HttpContext httpContext) =>
        httpContext.Items.TryGetValue(IdentityItemKey, out var value) && value is IngestionIdentity identity
            ? identity
            : throw new InvalidOperationException("The ingestion request has not been authenticated.");

    private static bool TryGetKeyId(string key, out string keyId)
    {
        keyId = string.Empty;
        if (!key.StartsWith(Prefix, StringComparison.Ordinal) || key.Length <= Prefix.Length + 2)
        {
            return false;
        }

        var separator = key.IndexOf('.', Prefix.Length);
        if (separator <= Prefix.Length || separator == key.Length - 1)
        {
            return false;
        }

        keyId = key[Prefix.Length..separator];
        return keyId.Length is >= 8 and <= 64;
    }

    private static bool KeysMatch(string expected, string supplied)
    {
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        return CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash);
    }

    private sealed record CredentialLookup(
        Guid Id,
        Guid ComponentId,
        byte[] KeyHash,
        DateTimeOffset? LastUsedAt);
}

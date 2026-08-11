using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure;
using Monitor.Infrastructure.Auditing;
using Monitor.Web.Auth;

namespace Monitor.Web.Pages;

public sealed class ComponentDetailModel(
    MonitorDbContext db,
    ComponentCredentialIssuer credentialIssuer,
    AuditTrailWriter audit) : PageModel
{
    public ComponentSummary? Component { get; private set; }
    public IReadOnlyList<CredentialRow> Credentials { get; private set; } = [];
    public string? IssuedCredentialKey { get; private set; }
    public string? IssuedCredentialName { get; private set; }

    [BindProperty]
    public CreateCredentialInput CredentialInput { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, cancellationToken))
        {
            return NotFound();
        }

        IssuedCredentialKey = TempData["IssuedComponentCredentialKey"] as string;
        IssuedCredentialName = TempData["IssuedComponentCredentialName"] as string;
        return Page();
    }

    public async Task<IActionResult> OnPostCreateCredentialAsync(Guid id, CancellationToken cancellationToken)
    {
        ModelState.Clear();
        if (!TryValidateModel(CredentialInput, nameof(CredentialInput)))
        {
            await LoadAsync(id, cancellationToken);
            return Page();
        }

        var component = await db.Components
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new { x.Id, x.Name, x.Slug, x.Environment })
            .SingleOrDefaultAsync(cancellationToken);
        if (component is null)
        {
            return NotFound();
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var issued = await credentialIssuer.IssueAsync(
                id,
                CredentialInput.Name,
                User.Identity?.Name,
                now,
                cancellationToken);

            audit.RecordOperator(
                User,
                AuditActions.ComponentCredentialIssued,
                AuditTargetTypes.ComponentCredential,
                issued.Credential.Id.ToString("D"),
                issued.Credential.Name,
                after: SnapshotCredential(issued.Credential),
                metadata: new
                {
                    componentId = component.Id,
                    component.Name,
                    component.Slug,
                    component.Environment
                },
                occurredAt: now);

            await db.SaveChangesAsync(cancellationToken);

            StoreIssuedCredential(issued);
            TempData["StatusMessage"] = "Component ingestion credential issued. Copy it now; Monitor stores only its hash.";
            return RedirectToPage(new { id });
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadAsync(id, cancellationToken);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostRotateCredentialAsync(
        Guid id,
        Guid credentialId,
        CancellationToken cancellationToken)
    {
        var credential = await db.ComponentIngestionCredentials
            .SingleOrDefaultAsync(
                x => x.Id == credentialId && x.ComponentId == id,
                cancellationToken);
        if (credential is null)
        {
            return NotFound();
        }

        if (credential.IsRevoked)
        {
            TempData["StatusMessage"] = "That credential is already revoked.";
            return RedirectToPage(new { id });
        }

        var before = SnapshotCredential(credential);
        var now = DateTimeOffset.UtcNow;
        var actor = User.Identity?.Name;
        credential.Revoke(actor, now);
        var replacement = await credentialIssuer.IssueAsync(
            id,
            credential.Name,
            actor,
            now,
            cancellationToken);

        audit.RecordOperator(
            User,
            AuditActions.ComponentCredentialRotated,
            AuditTargetTypes.ComponentCredential,
            credential.Id.ToString("D"),
            credential.Name,
            before,
            new
            {
                previous = SnapshotCredential(credential),
                replacement = SnapshotCredential(replacement.Credential)
            },
            new
            {
                componentId = id,
                replacementCredentialId = replacement.Credential.Id,
                replacementKeyId = replacement.Credential.KeyId
            },
            now);

        await db.SaveChangesAsync(cancellationToken);

        StoreIssuedCredential(replacement);
        TempData["StatusMessage"] = "Credential rotated. The previous key was revoked immediately; copy the replacement now.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRevokeCredentialAsync(
        Guid id,
        Guid credentialId,
        CancellationToken cancellationToken)
    {
        var credential = await db.ComponentIngestionCredentials
            .SingleOrDefaultAsync(
                x => x.Id == credentialId && x.ComponentId == id,
                cancellationToken);
        if (credential is null)
        {
            return NotFound();
        }

        if (!credential.IsRevoked)
        {
            var before = SnapshotCredential(credential);
            var now = DateTimeOffset.UtcNow;
            credential.Revoke(User.Identity?.Name, now);
            audit.RecordOperator(
                User,
                AuditActions.ComponentCredentialRevoked,
                AuditTargetTypes.ComponentCredential,
                credential.Id.ToString("D"),
                credential.Name,
                before,
                SnapshotCredential(credential),
                new { componentId = id },
                now);

            await db.SaveChangesAsync(cancellationToken);
        }

        TempData["StatusMessage"] = "Component ingestion credential revoked.";
        return RedirectToPage(new { id });
    }

    private async Task<bool> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var component = await db.Components
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (component is null)
        {
            return false;
        }

        Component = new ComponentSummary(
            component.Id,
            component.Name,
            component.Slug,
            component.Type,
            component.Environment,
            component.Version,
            component.Enabled,
            component.GetEffectiveStatus(now, TimeSpan.FromMinutes(2)),
            component.LastHeartbeatAt,
            component.LastRunAt,
            component.CreatedAt);

        Credentials = await db.ComponentIngestionCredentials
            .AsNoTracking()
            .Where(x => x.ComponentId == id)
            .OrderBy(x => x.RevokedAt != null)
            .ThenByDescending(x => x.CreatedAt)
            .Select(x => new CredentialRow(
                x.Id,
                x.Name,
                x.KeyId,
                x.CreatedAt,
                x.CreatedBy,
                x.LastUsedAt,
                x.RevokedAt,
                x.RevokedBy))
            .ToListAsync(cancellationToken);

        return true;
    }

    private void StoreIssuedCredential(IssuedComponentCredential issued)
    {
        TempData["IssuedComponentCredentialKey"] = issued.PlaintextKey;
        TempData["IssuedComponentCredentialName"] = issued.Credential.Name;
    }

    private static object SnapshotCredential(ComponentIngestionCredential credential) => new
    {
        credential.ComponentId,
        credential.Name,
        credential.KeyId,
        credential.CreatedAt,
        credential.CreatedBy,
        credential.LastUsedAt,
        credential.RevokedAt,
        credential.RevokedBy
    };

    public sealed class CreateCredentialInput
    {
        [Required, StringLength(200)]
        public string Name { get; set; } = "Primary ingestion";
    }

    public sealed record ComponentSummary(
        Guid Id,
        string Name,
        string Slug,
        ComponentType Type,
        string Environment,
        string? Version,
        bool Enabled,
        ComponentStatus Status,
        DateTimeOffset? LastHeartbeatAt,
        DateTimeOffset? LastRunAt,
        DateTimeOffset CreatedAt);

    public sealed record CredentialRow(
        Guid Id,
        string Name,
        string KeyId,
        DateTimeOffset CreatedAt,
        string? CreatedBy,
        DateTimeOffset? LastUsedAt,
        DateTimeOffset? RevokedAt,
        string? RevokedBy)
    {
        public bool IsRevoked => RevokedAt is not null;
        public string DisplayKeyId => $"mon_c_{KeyId[..Math.Min(8, KeyId.Length)]}…";
    }
}

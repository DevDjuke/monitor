using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Monitor.Domain;
using Monitor.Infrastructure;
using Monitor.Infrastructure.Auditing;
using Monitor.Infrastructure.Control;
using Monitor.Web.Auth;

namespace Monitor.Web.Pages;

public sealed class ComponentDetailModel(
    MonitorDbContext db,
    ComponentCredentialIssuer credentialIssuer,
    AuditTrailWriter audit,
    IOptions<ComponentCommandOptions> commandOptions) : PageModel
{
    private readonly ComponentCommandOptions _commandOptions = commandOptions.Value;

    public ComponentSummary? Component { get; private set; }
    public IReadOnlyList<CredentialRow> Credentials { get; private set; } = [];
    public IReadOnlyList<CommandRow> Commands { get; private set; } = [];
    public IReadOnlyList<ActiveRunOption> ActiveRuns { get; private set; } = [];
    public string? IssuedCredentialKey { get; private set; }
    public string? IssuedCredentialName { get; private set; }

    [BindProperty]
    public CreateCredentialInput CredentialInput { get; set; } = new();

    [BindProperty]
    public IssueCommandInput CommandInput { get; set; } = new();

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

    public async Task<IActionResult> OnPostIssueCommandAsync(Guid id, CancellationToken cancellationToken)
    {
        ModelState.Clear();
        if (!TryValidateModel(CommandInput, nameof(CommandInput)))
        {
            await LoadAsync(id, cancellationToken);
            return Page();
        }

        var component = await db.Components.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (component is null)
        {
            return NotFound();
        }

        if (CommandInput.Type == ComponentCommandType.KillRun)
        {
            if (CommandInput.TargetRunId is null ||
                !await db.Runs.AnyAsync(
                    x => x.Id == CommandInput.TargetRunId && x.ComponentId == id && x.Status == RunStatus.Running,
                    cancellationToken))
            {
                ModelState.AddModelError(nameof(CommandInput.TargetRunId), "Select a currently running run owned by this component.");
            }
        }
        else if (CommandInput.TargetRunId is not null)
        {
            ModelState.AddModelError(nameof(CommandInput.TargetRunId), "Only KillRun may target a run.");
        }

        if (CommandInput.Type == ComponentCommandType.RefreshConfiguration)
        {
            if (!string.IsNullOrWhiteSpace(CommandInput.PayloadJson))
            {
                try
                {
                    using var _ = JsonDocument.Parse(CommandInput.PayloadJson);
                }
                catch (JsonException)
                {
                    ModelState.AddModelError(nameof(CommandInput.PayloadJson), "Configuration payload must be valid JSON.");
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(CommandInput.PayloadJson))
        {
            ModelState.AddModelError(nameof(CommandInput.PayloadJson), "Only RefreshConfiguration accepts a JSON payload.");
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync(id, cancellationToken);
            return Page();
        }

        var now = DateTimeOffset.UtcNow;
        var expiryMinutes = CommandInput.ExpiryMinutes ?? Math.Clamp(_commandOptions.DefaultExpiryMinutes, 1, 24 * 60);
        var command = ComponentCommand.Create(
            id,
            CommandInput.Type,
            CommandInput.TargetRunId,
            CommandInput.PayloadJson,
            User.Identity?.Name,
            now,
            now.AddMinutes(expiryMinutes));

        db.ComponentCommands.Add(command);
        audit.RecordOperator(
            User,
            AuditActions.ComponentCommandIssued,
            AuditTargetTypes.ComponentCommand,
            command.Id.ToString("D"),
            command.Type.ToString(),
            after: ComponentCommandService.Snapshot(command),
            metadata: new
            {
                componentId = component.Id,
                component.Name,
                component.Environment,
                command.TargetRunId,
                hasPayload = !string.IsNullOrWhiteSpace(command.PayloadJson)
            },
            occurredAt: now);

        await db.SaveChangesAsync(cancellationToken);
        TempData["StatusMessage"] = $"{command.Type} command queued for {component.Name}.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostCancelCommandAsync(
        Guid id,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        var command = await db.ComponentCommands
            .Include(x => x.Component)
            .SingleOrDefaultAsync(x => x.Id == commandId && x.ComponentId == id, cancellationToken);
        if (command is null)
        {
            return NotFound();
        }

        if (command.IsTerminal)
        {
            TempData["StatusMessage"] = $"Command is already {command.Status}.";
            return RedirectToPage(new { id });
        }

        var before = ComponentCommandService.Snapshot(command);
        var now = DateTimeOffset.UtcNow;
        command.Cancel(User.Identity?.Name, now);
        audit.RecordOperator(
            User,
            AuditActions.ComponentCommandCancelled,
            AuditTargetTypes.ComponentCommand,
            command.Id.ToString("D"),
            command.Type.ToString(),
            before,
            ComponentCommandService.Snapshot(command),
            new { componentId = id, command.TargetRunId },
            now);

        await db.SaveChangesAsync(cancellationToken);
        TempData["StatusMessage"] = "Component command cancelled.";
        return RedirectToPage(new { id });
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
            component.ControlState,
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

        Commands = await db.ComponentCommands
            .AsNoTracking()
            .Where(x => x.ComponentId == id)
            .OrderByDescending(x => x.CreatedAt)
            .Take(50)
            .Select(x => new CommandRow(
                x.Id,
                x.Type,
                x.Status,
                x.TargetRunId,
                x.RequestedBy,
                x.CreatedAt,
                x.ExpiresAt,
                x.LeasedAt,
                x.LeaseExpiresAt,
                x.DeliveryAttempts,
                x.CompletedAt,
                x.ResultJson,
                x.Error))
            .ToListAsync(cancellationToken);

        ActiveRuns = await db.Runs
            .AsNoTracking()
            .Where(x => x.ComponentId == id && x.Status == RunStatus.Running)
            .OrderByDescending(x => x.StartedAt)
            .Select(x => new ActiveRunOption(x.Id, x.Name, x.Sequence, x.StartedAt))
            .ToListAsync(cancellationToken);

        if (CommandInput.ExpiryMinutes is null)
        {
            CommandInput.ExpiryMinutes = Math.Clamp(_commandOptions.DefaultExpiryMinutes, 1, 24 * 60);
        }

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

    public sealed class IssueCommandInput
    {
        [Required]
        public ComponentCommandType Type { get; set; } = ComponentCommandType.Pause;

        public Guid? TargetRunId { get; set; }

        [StringLength(65536)]
        public string? PayloadJson { get; set; }

        [Range(1, 1440)]
        public int? ExpiryMinutes { get; set; }
    }

    public sealed record ComponentSummary(
        Guid Id,
        string Name,
        string Slug,
        ComponentType Type,
        string Environment,
        string? Version,
        bool Enabled,
        ComponentControlState ControlState,
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

    public sealed record CommandRow(
        Guid Id,
        ComponentCommandType Type,
        ComponentCommandStatus Status,
        Guid? TargetRunId,
        string? RequestedBy,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? LeasedAt,
        DateTimeOffset? LeaseExpiresAt,
        int DeliveryAttempts,
        DateTimeOffset? CompletedAt,
        string? ResultJson,
        string? Error)
    {
        public bool CanCancel => Status is ComponentCommandStatus.Pending or ComponentCommandStatus.Leased;
    }

    public sealed record ActiveRunOption(Guid Id, string Name, long Sequence, DateTimeOffset StartedAt);
}

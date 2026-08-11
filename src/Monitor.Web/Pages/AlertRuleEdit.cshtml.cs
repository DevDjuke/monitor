using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure;
using Monitor.Infrastructure.Auditing;

namespace Monitor.Web.Pages;

public sealed class AlertRuleEditModel(MonitorDbContext db, AuditTrailWriter audit) : PageModel
{
    public Guid? RuleId { get; private set; }
    public bool IsEdit => RuleId is not null;
    public IReadOnlyList<FailureGroupOption> FailureGroups { get; private set; } = [];
    public IReadOnlyList<DestinationOption> Destinations { get; private set; } = [];
    public FailureGroupOption? SelectedFailureGroup { get; private set; }

    [BindProperty]
    public AlertRuleInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid? id, Guid? failureGroupId, CancellationToken cancellationToken)
    {
        RuleId = id;

        if (id is not null)
        {
            var rule = await db.FailureAlertRules
                .AsNoTracking()
                .Include(x => x.DestinationAssignments)
                .SingleOrDefaultAsync(x => x.Id == id.Value, cancellationToken);

            if (rule is null || rule.IsDeleted)
            {
                return NotFound();
            }

            Input = new AlertRuleInput
            {
                FailureGroupId = rule.FailureGroupId,
                Name = rule.Name,
                Threshold = rule.Threshold,
                WindowMinutes = rule.WindowMinutes,
                CooldownMinutes = rule.CooldownMinutes,
                Enabled = rule.Enabled,
                DeliverToAllEnabledDestinations = rule.DeliverToAllEnabledDestinations,
                SelectedDestinationIds = rule.DestinationAssignments.Select(x => x.DestinationId).ToList()
            };
        }
        else if (failureGroupId is not null)
        {
            Input.FailureGroupId = failureGroupId.Value;
        }

        await LoadSupportDataAsync(cancellationToken);

        if (Input.FailureGroupId != Guid.Empty && SelectedFailureGroup is null)
        {
            return NotFound();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(Guid? id, CancellationToken cancellationToken)
    {
        RuleId = id;
        Input.SelectedDestinationIds ??= [];
        Input.SelectedDestinationIds = Input.SelectedDestinationIds.Distinct().ToList();

        if (!Input.DeliverToAllEnabledDestinations && Input.SelectedDestinationIds.Count == 0)
        {
            ModelState.AddModelError(
                "Input.SelectedDestinationIds",
                "Select at least one delivery destination, or use all enabled destinations.");
        }

        FailureAlertRule? existingRule = null;
        object? before = null;
        if (id is not null)
        {
            existingRule = await db.FailureAlertRules
                .Include(x => x.DestinationAssignments)
                .SingleOrDefaultAsync(x => x.Id == id.Value, cancellationToken);

            if (existingRule is null || existingRule.IsDeleted)
            {
                return NotFound();
            }

            before = SnapshotRule(existingRule);

            // A rule's failure fingerprint is immutable once created. Historical alert events
            // and evidence remain semantically tied to that fingerprint.
            Input.FailureGroupId = existingRule.FailureGroupId;
        }

        var failureGroup = await db.FailureGroups
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == Input.FailureGroupId, cancellationToken);

        if (failureGroup is null)
        {
            ModelState.AddModelError("Input.FailureGroupId", "Select a valid failure fingerprint.");
        }

        if (!Input.DeliverToAllEnabledDestinations && Input.SelectedDestinationIds.Count > 0)
        {
            var validDestinationCount = await db.AlertDeliveryDestinations
                .LongCountAsync(x => Input.SelectedDestinationIds.Contains(x.Id), cancellationToken);

            if (validDestinationCount != Input.SelectedDestinationIds.Count)
            {
                ModelState.AddModelError("Input.SelectedDestinationIds", "One or more selected delivery destinations no longer exist.");
            }
        }

        if (!ModelState.IsValid || failureGroup is null)
        {
            await LoadSupportDataAsync(cancellationToken);
            return Page();
        }

        var now = DateTimeOffset.UtcNow;
        var name = string.IsNullOrWhiteSpace(Input.Name)
            ? $"{failureGroup.Category}: {failureGroup.Operation}"
            : Input.Name.Trim();

        try
        {
            FailureAlertRule rule;
            if (existingRule is null)
            {
                rule = FailureAlertRule.Create(
                    failureGroup.Id,
                    name,
                    Input.Threshold,
                    Input.WindowMinutes,
                    Input.CooldownMinutes,
                    now);

                rule.SetEnabled(Input.Enabled, now);
                rule.SetDeliveryScope(Input.DeliverToAllEnabledDestinations, now);
                db.FailureAlertRules.Add(rule);
            }
            else
            {
                rule = existingRule;
                rule.Update(
                    name,
                    Input.Threshold,
                    Input.WindowMinutes,
                    Input.CooldownMinutes,
                    now);
                rule.SetEnabled(Input.Enabled, now);
                rule.SetDeliveryScope(Input.DeliverToAllEnabledDestinations, now);
            }

            await SyncDestinationAssignmentsAsync(rule, cancellationToken);

            var after = SnapshotRule(
                rule,
                rule.DeliverToAllEnabledDestinations ? [] : Input.SelectedDestinationIds);
            audit.RecordOperator(
                User,
                existingRule is null ? AuditActions.AlertRuleCreated : AuditActions.AlertRuleUpdated,
                AuditTargetTypes.AlertRule,
                rule.Id.ToString("D"),
                rule.Name,
                before,
                after,
                new
                {
                    failureGroupId = failureGroup.Id,
                    failureGroup.Fingerprint,
                    failureGroup.Category,
                    failureGroup.Operation
                },
                now);

            await db.SaveChangesAsync(cancellationToken);

            TempData["StatusMessage"] = existingRule is null
                ? "Alert rule created."
                : "Alert rule updated.";

            return RedirectToPage("/AlertRules");
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadSupportDataAsync(cancellationToken);
            return Page();
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadSupportDataAsync(cancellationToken);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var rule = await db.FailureAlertRules
            .Include(x => x.DestinationAssignments)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (rule is null)
        {
            return NotFound();
        }

        var before = SnapshotRule(rule);
        var now = DateTimeOffset.UtcNow;
        rule.Delete(now);
        audit.RecordOperator(
            User,
            AuditActions.AlertRuleDeleted,
            AuditTargetTypes.AlertRule,
            rule.Id.ToString("D"),
            rule.Name,
            before,
            SnapshotRule(rule),
            occurredAt: now);

        await db.SaveChangesAsync(cancellationToken);
        TempData["StatusMessage"] = "Alert rule deleted. Historical alert events remain available.";
        return RedirectToPage("/AlertRules");
    }

    private async Task SyncDestinationAssignmentsAsync(FailureAlertRule rule, CancellationToken cancellationToken)
    {
        var existingAssignments = rule.DestinationAssignments.ToList();

        if (rule.DeliverToAllEnabledDestinations)
        {
            if (existingAssignments.Count > 0)
            {
                db.FailureAlertRuleDestinations.RemoveRange(existingAssignments);
            }

            return;
        }

        var desired = Input.SelectedDestinationIds.ToHashSet();
        var toRemove = existingAssignments.Where(x => !desired.Contains(x.DestinationId)).ToList();
        if (toRemove.Count > 0)
        {
            db.FailureAlertRuleDestinations.RemoveRange(toRemove);
        }

        var existingIds = existingAssignments.Select(x => x.DestinationId).ToHashSet();
        foreach (var destinationId in desired.Where(x => !existingIds.Contains(x)))
        {
            db.FailureAlertRuleDestinations.Add(FailureAlertRuleDestination.Create(rule.Id, destinationId));
        }

        await Task.CompletedTask;
    }

    private async Task LoadSupportDataAsync(CancellationToken cancellationToken)
    {
        Destinations = await db.AlertDeliveryDestinations
            .AsNoTracking()
            .OrderByDescending(x => x.Enabled)
            .ThenBy(x => x.Name)
            .Select(x => new DestinationOption(
                x.Id,
                x.Name,
                x.Kind,
                x.EndpointUrl,
                x.Enabled))
            .ToListAsync(cancellationToken);

        var groups = await db.FailureGroups
            .AsNoTracking()
            .OrderByDescending(x => x.LastSeenAt)
            .Take(300)
            .Select(x => new FailureGroupOption(
                x.Id,
                x.Category,
                x.Operation,
                x.FailureType,
                x.Dependency,
                x.Fingerprint,
                x.Occurrences,
                x.LastSeenAt))
            .ToListAsync(cancellationToken);

        if (Input.FailureGroupId != Guid.Empty && groups.All(x => x.Id != Input.FailureGroupId))
        {
            var selected = await db.FailureGroups
                .AsNoTracking()
                .Where(x => x.Id == Input.FailureGroupId)
                .Select(x => new FailureGroupOption(
                    x.Id,
                    x.Category,
                    x.Operation,
                    x.FailureType,
                    x.Dependency,
                    x.Fingerprint,
                    x.Occurrences,
                    x.LastSeenAt))
                .SingleOrDefaultAsync(cancellationToken);

            if (selected is not null)
            {
                groups.Insert(0, selected);
            }
        }

        FailureGroups = groups;
        SelectedFailureGroup = groups.SingleOrDefault(x => x.Id == Input.FailureGroupId);
    }

    private static object SnapshotRule(FailureAlertRule rule, IEnumerable<Guid>? destinationIds = null) => new
    {
        rule.FailureGroupId,
        rule.Name,
        rule.Threshold,
        rule.WindowMinutes,
        rule.CooldownMinutes,
        rule.Enabled,
        rule.DeliverToAllEnabledDestinations,
        destinationIds = (destinationIds ?? rule.DestinationAssignments.Select(x => x.DestinationId))
            .OrderBy(x => x)
            .ToArray(),
        rule.IsDeleted,
        rule.DeletedAt
    };

    public sealed class AlertRuleInput
    {
        [Required]
        public Guid FailureGroupId { get; set; }

        [StringLength(200)]
        public string? Name { get; set; }

        [Range(1, 100_000)]
        public int Threshold { get; set; } = 5;

        [Range(1, 10_080)]
        public int WindowMinutes { get; set; } = 10;

        [Range(0, 10_080)]
        public int CooldownMinutes { get; set; } = 15;

        public bool Enabled { get; set; } = true;
        public bool DeliverToAllEnabledDestinations { get; set; } = true;
        public List<Guid> SelectedDestinationIds { get; set; } = [];
    }

    public sealed record FailureGroupOption(
        Guid Id,
        FailureCategory Category,
        string Operation,
        string? FailureType,
        string? Dependency,
        string Fingerprint,
        long Occurrences,
        DateTimeOffset LastSeenAt);

    public sealed record DestinationOption(
        Guid Id,
        string Name,
        AlertDeliveryKind Kind,
        string EndpointUrl,
        bool Enabled);
}

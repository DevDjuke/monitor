using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure;
using Monitor.Infrastructure.Auditing;
using Monitor.Infrastructure.Usage;

namespace Monitor.Web.Pages;

public sealed class BudgetEditModel(MonitorDbContext db, AuditTrailWriter audit) : PageModel
{
    private readonly UsageBudgetEnforcementPolicyStore _enforcementPolicies = new(db);

    public Guid? BudgetId { get; private set; }
    public bool IsEdit => BudgetId is not null;
    public IReadOnlyList<ComponentOption> Components { get; private set; } = [];
    public IReadOnlyList<string> Environments { get; private set; } = [];
    public IReadOnlyList<string> Models { get; private set; } = [];
    public IReadOnlyList<DestinationOption> Destinations { get; private set; } = [];

    [BindProperty] public BudgetInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid? id, CancellationToken cancellationToken)
    {
        BudgetId = id;
        if (id is not null)
        {
            var budget = await db.UsageBudgets
                .AsNoTracking()
                .Include(x => x.DestinationAssignments)
                .SingleOrDefaultAsync(x => x.Id == id.Value, cancellationToken);
            if (budget is null || budget.IsDeleted) return NotFound();

            Input = new BudgetInput
            {
                Name = budget.Name,
                ComponentId = budget.ComponentId,
                Environment = budget.Environment,
                Model = budget.Model,
                Period = budget.Period,
                CostLimitUsd = budget.CostLimitUsd,
                TokenLimit = budget.TokenLimit,
                WarningPercent = budget.WarningPercent,
                CriticalPercent = budget.CriticalPercent,
                Enabled = budget.Enabled,
                DeliverToAllEnabledDestinations = budget.DeliverToAllEnabledDestinations,
                SelectedDestinationIds = budget.DestinationAssignments.Select(x => x.DestinationId).ToList(),
                CriticalAction = await _enforcementPolicies.GetCriticalActionAsync(budget.Id, cancellationToken)
            };
        }

        await LoadSupportDataAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(Guid? id, CancellationToken cancellationToken)
    {
        BudgetId = id;
        Input.SelectedDestinationIds ??= [];
        Input.SelectedDestinationIds = Input.SelectedDestinationIds.Distinct().ToList();
        Input.Environment = Normalize(Input.Environment);
        Input.Model = Normalize(Input.Model);

        if (Input.CostLimitUsd is null && Input.TokenLimit is null)
            ModelState.AddModelError(string.Empty, "Configure a cost limit, token limit, or both.");
        if (Input.WarningPercent >= Input.CriticalPercent)
            ModelState.AddModelError(nameof(Input.WarningPercent), "Warning threshold must be lower than critical threshold.");
        if (!Input.DeliverToAllEnabledDestinations && Input.SelectedDestinationIds.Count == 0)
            ModelState.AddModelError(nameof(Input.SelectedDestinationIds), "Select at least one destination, or use all enabled destinations.");
        if (!Enum.IsDefined(Input.CriticalAction))
            ModelState.AddModelError(nameof(Input.CriticalAction), "Select a valid critical action.");
        if (Input.CriticalAction != UsageBudgetEnforcementAction.None && Input.ComponentId is null)
            ModelState.AddModelError(nameof(Input.CriticalAction), "Automatic enforcement requires a budget scoped to one component.");
        if (Input.ComponentId is not null && !await db.Components.AnyAsync(x => x.Id == Input.ComponentId.Value, cancellationToken))
            ModelState.AddModelError(nameof(Input.ComponentId), "Select a valid component.");
        if (!Input.DeliverToAllEnabledDestinations && Input.SelectedDestinationIds.Count > 0)
        {
            var valid = await db.AlertDeliveryDestinations.LongCountAsync(x => Input.SelectedDestinationIds.Contains(x.Id), cancellationToken);
            if (valid != Input.SelectedDestinationIds.Count)
                ModelState.AddModelError(nameof(Input.SelectedDestinationIds), "One or more destinations no longer exist.");
        }

        UsageBudget? existing = null;
        var previousCriticalAction = UsageBudgetEnforcementAction.None;
        if (id is not null)
        {
            existing = await db.UsageBudgets.Include(x => x.DestinationAssignments).SingleOrDefaultAsync(x => x.Id == id.Value, cancellationToken);
            if (existing is null || existing.IsDeleted) return NotFound();
            previousCriticalAction = await _enforcementPolicies.GetCriticalActionAsync(existing.Id, cancellationToken);
        }

        if (!ModelState.IsValid)
        {
            await LoadSupportDataAsync(cancellationToken);
            return Page();
        }

        var now = DateTimeOffset.UtcNow;
        var before = existing is null ? null : Snapshot(existing, previousCriticalAction);
        try
        {
            var budget = existing ?? UsageBudget.Create(
                Input.Name,
                Input.ComponentId,
                Input.Environment,
                Input.Model,
                Input.Period,
                Input.CostLimitUsd,
                Input.TokenLimit,
                Input.WarningPercent,
                Input.CriticalPercent,
                now);

            if (existing is null)
            {
                db.UsageBudgets.Add(budget);
            }
            else
            {
                budget.Update(Input.Name, Input.ComponentId, Input.Environment, Input.Model, Input.Period, Input.CostLimitUsd, Input.TokenLimit, Input.WarningPercent, Input.CriticalPercent, now);
            }

            budget.SetEnabled(Input.Enabled, now);
            budget.SetDeliveryScope(Input.DeliverToAllEnabledDestinations, now);
            SyncAssignments(budget);

            audit.RecordOperator(
                User,
                existing is null ? "usage-budget.created" : "usage-budget.updated",
                "UsageBudget",
                budget.Id.ToString(),
                budget.Name,
                before,
                Snapshot(budget, Input.CriticalAction),
                metadata: new
                {
                    destinationIds = Input.DeliverToAllEnabledDestinations ? null : Input.SelectedDestinationIds,
                    criticalAction = Input.CriticalAction
                },
                occurredAt: now);

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await _enforcementPolicies.SetCriticalActionAsync(budget.Id, Input.CriticalAction, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            TempData["StatusMessage"] = existing is null ? "Usage budget created." : "Usage budget updated.";
            return RedirectToPage("/Budgets");
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

    private void SyncAssignments(UsageBudget budget)
    {
        var existing = budget.DestinationAssignments.ToList();
        if (budget.DeliverToAllEnabledDestinations)
        {
            if (existing.Count > 0) db.UsageBudgetDestinations.RemoveRange(existing);
            return;
        }

        var desired = Input.SelectedDestinationIds.ToHashSet();
        var remove = existing.Where(x => !desired.Contains(x.DestinationId)).ToList();
        if (remove.Count > 0) db.UsageBudgetDestinations.RemoveRange(remove);
        var existingIds = existing.Select(x => x.DestinationId).ToHashSet();
        foreach (var destinationId in desired.Where(x => !existingIds.Contains(x)))
            db.UsageBudgetDestinations.Add(UsageBudgetDestination.Create(budget.Id, destinationId));
    }

    private async Task LoadSupportDataAsync(CancellationToken cancellationToken)
    {
        Components = await db.Components.AsNoTracking().OrderBy(x => x.Name).ThenBy(x => x.Environment)
            .Select(x => new ComponentOption(x.Id, x.Name, x.Environment)).ToListAsync(cancellationToken);
        Environments = await db.Components.AsNoTracking().Select(x => x.Environment).Where(x => x != "").Distinct().OrderBy(x => x).ToListAsync(cancellationToken);
        var rawModels = await db.Runs.AsNoTracking().Where(x => x.Model != null && x.Model != "").Select(x => x.Model!).Distinct().ToListAsync(cancellationToken);
        var aggregateModels = await db.RunAggregates.AsNoTracking().Where(x => x.Model != "").Select(x => x.Model).Distinct().ToListAsync(cancellationToken);
        Models = rawModels.Concat(aggregateModels).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        Destinations = await db.AlertDeliveryDestinations.AsNoTracking().OrderByDescending(x => x.Enabled).ThenBy(x => x.Name)
            .Select(x => new DestinationOption(x.Id, x.Name, x.EndpointUrl, x.Enabled)).ToListAsync(cancellationToken);
    }

    private static object Snapshot(UsageBudget x, UsageBudgetEnforcementAction criticalAction) => new
    {
        x.Name,
        x.ComponentId,
        x.Environment,
        x.Model,
        x.Period,
        x.CostLimitUsd,
        x.TokenLimit,
        x.WarningPercent,
        x.CriticalPercent,
        x.Enabled,
        x.DeliverToAllEnabledDestinations,
        CriticalAction = criticalAction
    };

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public sealed class BudgetInput
    {
        [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
        public Guid? ComponentId { get; set; }
        [StringLength(80)] public string? Environment { get; set; }
        [StringLength(160)] public string? Model { get; set; }
        public UsageBudgetPeriod Period { get; set; } = UsageBudgetPeriod.Monthly;
        [Range(0.000001, 1_000_000_000)] public double? CostLimitUsd { get; set; }
        [Range(1, long.MaxValue)] public long? TokenLimit { get; set; }
        [Range(1, 1000)] public int WarningPercent { get; set; } = 80;
        [Range(1, 1000)] public int CriticalPercent { get; set; } = 100;
        public UsageBudgetEnforcementAction CriticalAction { get; set; } = UsageBudgetEnforcementAction.None;
        public bool Enabled { get; set; } = true;
        public bool DeliverToAllEnabledDestinations { get; set; } = true;
        public List<Guid> SelectedDestinationIds { get; set; } = [];
    }

    public sealed record ComponentOption(Guid Id, string Name, string Environment);
    public sealed record DestinationOption(Guid Id, string Name, string EndpointUrl, bool Enabled);
}

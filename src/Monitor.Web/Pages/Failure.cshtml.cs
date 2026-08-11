using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure;
using Monitor.Infrastructure.Auditing;

namespace Monitor.Web.Pages;

public sealed class FailureModel(MonitorDbContext db, AuditTrailWriter audit) : PageModel
{
    public FailureSummary? Failure { get; private set; }
    public IReadOnlyList<TrendBucket> Trend { get; private set; } = [];
    public IReadOnlyList<OccurrenceRow> RecentOccurrences { get; private set; } = [];
    public IReadOnlyList<AlertRuleRow> AlertRules { get; private set; } = [];
    public IReadOnlyList<AlertEventRow> RecentAlertEvents { get; private set; } = [];

    public long Last15Minutes { get; private set; }
    public long LastHour { get; private set; }
    public long Last24Hours { get; private set; }
    public long PeakHourly { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, cancellationToken))
        {
            return NotFound();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostToggleAlertAsync(Guid id, Guid ruleId, CancellationToken cancellationToken)
    {
        var rule = await db.FailureAlertRules
            .SingleOrDefaultAsync(
                x => x.Id == ruleId && x.FailureGroupId == id && !x.IsDeleted,
                cancellationToken);

        if (rule is null)
        {
            return NotFound();
        }

        var beforeEnabled = rule.Enabled;
        var now = DateTimeOffset.UtcNow;
        rule.SetEnabled(!rule.Enabled, now);
        audit.RecordOperator(
            User,
            rule.Enabled ? AuditActions.AlertRuleEnabled : AuditActions.AlertRuleDisabled,
            AuditTargetTypes.AlertRule,
            rule.Id.ToString("D"),
            rule.Name,
            new { enabled = beforeEnabled },
            new { rule.Enabled },
            new { failureGroupId = id },
            now);

        await db.SaveChangesAsync(cancellationToken);
        TempData["StatusMessage"] = rule.Enabled ? "Alert rule enabled." : "Alert rule disabled.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostAcknowledgeAsync(Guid id, Guid eventId, CancellationToken cancellationToken)
    {
        var alertEvent = await db.FailureAlertEvents
            .SingleOrDefaultAsync(x => x.Id == eventId && x.FailureGroupId == id, cancellationToken);

        if (alertEvent is null)
        {
            return NotFound();
        }

        if (alertEvent.AcknowledgedAt is null)
        {
            var now = DateTimeOffset.UtcNow;
            alertEvent.Acknowledge(User.Identity?.Name, now);
            audit.RecordOperator(
                User,
                AuditActions.AlertAcknowledged,
                AuditTargetTypes.Alert,
                alertEvent.Id.ToString("D"),
                before: new { acknowledgedAt = (DateTimeOffset?)null, acknowledgedBy = (string?)null },
                after: new { alertEvent.AcknowledgedAt, alertEvent.AcknowledgedBy },
                metadata: new { failureGroupId = id, alertEvent.AlertRuleId },
                occurredAt: now);

            await db.SaveChangesAsync(cancellationToken);
        }

        TempData["StatusMessage"] = "Alert acknowledged.";
        return RedirectToPage(new { id });
    }

    private async Task<bool> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        var group = await db.FailureGroups
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new FailureSummary(
                x.Id,
                x.Fingerprint,
                x.Category,
                x.FailureType,
                x.Operation,
                x.Dependency,
                x.HttpStatusCode,
                x.MessageTemplate,
                x.Occurrences,
                x.FirstSeenAt,
                x.LastSeenAt))
            .SingleOrDefaultAsync(cancellationToken);

        if (group is null)
        {
            return false;
        }

        Failure = group;

        var now = DateTimeOffset.UtcNow;
        var since24Hours = now.AddHours(-24);
        var occurrenceTimes = await db.Runs
            .AsNoTracking()
            .Where(x =>
                x.FailureGroupId == id &&
                x.CompletedAt != null &&
                x.CompletedAt >= since24Hours)
            .Select(x => x.CompletedAt!.Value)
            .ToListAsync(cancellationToken);

        Last15Minutes = occurrenceTimes.LongCount(x => x >= now.AddMinutes(-15));
        LastHour = occurrenceTimes.LongCount(x => x >= now.AddHours(-1));
        Last24Hours = occurrenceTimes.LongCount();

        Trend = BuildTrend(occurrenceTimes, now);
        PeakHourly = Trend.Count == 0 ? 0 : Trend.Max(x => x.Occurrences);

        RecentOccurrences = await db.Runs
            .AsNoTracking()
            .Where(x => x.FailureGroupId == id)
            .OrderByDescending(x => x.Sequence)
            .Take(50)
            .Select(x => new OccurrenceRow(
                x.Id,
                x.Sequence,
                x.Component.Name,
                x.Component.Environment,
                x.Name,
                x.Status,
                x.Model,
                x.CompletedAt,
                x.Error))
            .ToListAsync(cancellationToken);

        AlertRules = await db.FailureAlertRules
            .AsNoTracking()
            .Where(x => x.FailureGroupId == id && !x.IsDeleted)
            .OrderByDescending(x => x.Enabled)
            .ThenBy(x => x.CreatedAt)
            .Select(x => new AlertRuleRow(
                x.Id,
                x.Name,
                x.Threshold,
                x.WindowMinutes,
                x.CooldownMinutes,
                x.Enabled,
                x.DeliverToAllEnabledDestinations,
                x.DestinationAssignments.LongCount(),
                x.LastEvaluatedAt,
                x.LastTriggeredAt,
                x.Events.LongCount(e => e.AcknowledgedAt == null)))
            .ToListAsync(cancellationToken);

        RecentAlertEvents = await db.FailureAlertEvents
            .AsNoTracking()
            .Where(x => x.FailureGroupId == id)
            .OrderByDescending(x => x.TriggeredAt)
            .Take(20)
            .Select(x => new AlertEventRow(
                x.Id,
                x.AlertRule.Name,
                x.TriggeredAt,
                x.WindowStart,
                x.WindowEnd,
                x.OccurrencesInWindow,
                x.Threshold,
                x.AcknowledgedAt,
                x.AcknowledgedBy))
            .ToListAsync(cancellationToken);

        return true;
    }

    private static IReadOnlyList<TrendBucket> BuildTrend(IEnumerable<DateTimeOffset> occurrenceTimes, DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        var currentHour = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
        var counts = occurrenceTimes
            .Select(timestamp => timestamp.ToUniversalTime())
            .GroupBy(timestamp => new DateTimeOffset(
                timestamp.Year,
                timestamp.Month,
                timestamp.Day,
                timestamp.Hour,
                0,
                0,
                TimeSpan.Zero))
            .ToDictionary(group => group.Key, group => group.LongCount());

        var buckets = new List<TrendBucket>(24);
        for (var offset = 23; offset >= 0; offset--)
        {
            var bucketStart = currentHour.AddHours(-offset);
            buckets.Add(new TrendBucket(
                bucketStart,
                counts.GetValueOrDefault(bucketStart)));
        }

        return buckets;
    }

    public sealed record FailureSummary(
        Guid Id,
        string Fingerprint,
        FailureCategory Category,
        string? FailureType,
        string Operation,
        string? Dependency,
        int? HttpStatusCode,
        string? MessageTemplate,
        long Occurrences,
        DateTimeOffset FirstSeenAt,
        DateTimeOffset LastSeenAt);

    public sealed record TrendBucket(DateTimeOffset Start, long Occurrences);

    public sealed record OccurrenceRow(
        Guid RunId,
        long Sequence,
        string ComponentName,
        string Environment,
        string Name,
        RunStatus Status,
        string? Model,
        DateTimeOffset? CompletedAt,
        string? Error);

    public sealed record AlertRuleRow(
        Guid Id,
        string Name,
        int Threshold,
        int WindowMinutes,
        int CooldownMinutes,
        bool Enabled,
        bool DeliverToAllEnabledDestinations,
        long DestinationCount,
        DateTimeOffset? LastEvaluatedAt,
        DateTimeOffset? LastTriggeredAt,
        long OpenAlerts);

    public sealed record AlertEventRow(
        Guid Id,
        string RuleName,
        DateTimeOffset TriggeredAt,
        DateTimeOffset WindowStart,
        DateTimeOffset WindowEnd,
        long OccurrencesInWindow,
        int Threshold,
        DateTimeOffset? AcknowledgedAt,
        string? AcknowledgedBy);
}

using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure;

namespace Monitor.Web.Pages;

public sealed class AuditModel(MonitorDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string Window { get; set; } = "7d";

    [BindProperty(SupportsGet = true)]
    public AuditActorType? ActorType { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Actor { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Action { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? TargetType { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? TargetId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public int Take { get; set; } = 200;

    public IReadOnlyList<AuditActorType> ActorTypes { get; } = Enum.GetValues<AuditActorType>();
    public IReadOnlyList<string> Actions { get; private set; } = [];
    public IReadOnlyList<string> TargetTypes { get; private set; } = [];
    public IReadOnlyList<AuditRow> Rows { get; private set; } = [];
    public long MatchingCount { get; private set; }
    public long OperatorCount { get; private set; }
    public long SystemCount { get; private set; }
    public long ChangedStateCount { get; private set; }
    public string ScopeLabel { get; private set; } = "Last 7 days";

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        NormalizeFilters();

        Actions = await db.AuditEvents
            .AsNoTracking()
            .Select(x => x.Action)
            .Distinct()
            .OrderBy(x => x)
            .Take(200)
            .ToListAsync(cancellationToken);

        TargetTypes = await db.AuditEvents
            .AsNoTracking()
            .Select(x => x.TargetType)
            .Distinct()
            .OrderBy(x => x)
            .Take(100)
            .ToListAsync(cancellationToken);

        var query = ApplyFilters(db.AuditEvents.AsNoTracking());
        MatchingCount = await query.LongCountAsync(cancellationToken);
        OperatorCount = await query.LongCountAsync(x => x.ActorType == AuditActorType.Operator, cancellationToken);
        SystemCount = await query.LongCountAsync(x => x.ActorType == AuditActorType.System, cancellationToken);
        ChangedStateCount = await query.LongCountAsync(
            x => x.BeforeJson != null || x.AfterJson != null,
            cancellationToken);

        Rows = await query
            .OrderByDescending(x => x.OccurredAt)
            .ThenByDescending(x => x.Id)
            .Take(Take)
            .Select(x => new AuditRow(
                x.Id,
                x.OccurredAt,
                x.ActorType,
                x.ActorId,
                x.ActorName,
                x.Action,
                x.TargetType,
                x.TargetId,
                x.TargetName,
                x.BeforeJson,
                x.AfterJson,
                x.MetadataJson))
            .ToListAsync(cancellationToken);

        ScopeLabel = BuildScopeLabel();
    }

    private IQueryable<AuditEvent> ApplyFilters(IQueryable<AuditEvent> query)
    {
        var since = ResolveWindowStart(DateTimeOffset.UtcNow, Window);
        if (since is not null)
        {
            query = query.Where(x => x.OccurredAt >= since.Value);
        }

        if (ActorType is not null)
        {
            query = query.Where(x => x.ActorType == ActorType.Value);
        }

        if (!string.IsNullOrWhiteSpace(Actor))
        {
            var actor = Actor;
            query = query.Where(x =>
                (x.ActorName != null && x.ActorName.Contains(actor)) ||
                (x.ActorId != null && x.ActorId.Contains(actor)));
        }

        if (!string.IsNullOrWhiteSpace(Action))
        {
            query = query.Where(x => x.Action == Action);
        }

        if (!string.IsNullOrWhiteSpace(TargetType))
        {
            query = query.Where(x => x.TargetType == TargetType);
        }

        if (!string.IsNullOrWhiteSpace(TargetId))
        {
            var targetId = TargetId;
            query = query.Where(x => x.TargetId != null && x.TargetId.Contains(targetId));
        }

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var search = Search;
            query = query.Where(x =>
                x.Action.Contains(search) ||
                x.TargetType.Contains(search) ||
                (x.TargetId != null && x.TargetId.Contains(search)) ||
                (x.TargetName != null && x.TargetName.Contains(search)) ||
                (x.ActorName != null && x.ActorName.Contains(search)) ||
                (x.BeforeJson != null && x.BeforeJson.Contains(search)) ||
                (x.AfterJson != null && x.AfterJson.Contains(search)) ||
                (x.MetadataJson != null && x.MetadataJson.Contains(search)));
        }

        return query;
    }

    private void NormalizeFilters()
    {
        Window = Window?.Trim().ToLowerInvariant() switch
        {
            "24h" => "24h",
            "7d" => "7d",
            "30d" => "30d",
            "90d" => "90d",
            "all" => "all",
            _ => "7d"
        };

        Actor = Normalize(Actor);
        Action = Normalize(Action);
        TargetType = Normalize(TargetType);
        TargetId = Normalize(TargetId);
        Search = Normalize(Search);
        Take = Take is 50 or 100 or 200 or 500 ? Take : 200;
    }

    private string BuildScopeLabel()
    {
        var parts = new List<string>
        {
            Window switch
            {
                "24h" => "Last 24 hours",
                "7d" => "Last 7 days",
                "30d" => "Last 30 days",
                "90d" => "Last 90 days",
                _ => "All audit history"
            }
        };

        if (ActorType is not null)
        {
            parts.Add(ActorType.Value.ToString());
        }

        if (!string.IsNullOrWhiteSpace(Action))
        {
            parts.Add(Action);
        }

        if (!string.IsNullOrWhiteSpace(TargetType))
        {
            parts.Add(TargetType);
        }

        return string.Join(" · ", parts);
    }

    private static DateTimeOffset? ResolveWindowStart(DateTimeOffset now, string window) => window switch
    {
        "24h" => now.AddHours(-24),
        "7d" => now.AddDays(-7),
        "30d" => now.AddDays(-30),
        "90d" => now.AddDays(-90),
        _ => null
    };

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string FormatJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return json;
        }
    }

    public sealed record AuditRow(
        Guid Id,
        DateTimeOffset OccurredAt,
        AuditActorType ActorType,
        string? ActorId,
        string? ActorName,
        string Action,
        string TargetType,
        string? TargetId,
        string? TargetName,
        string? BeforeJson,
        string? AfterJson,
        string? MetadataJson)
    {
        public bool HasDetails =>
            !string.IsNullOrWhiteSpace(BeforeJson) ||
            !string.IsNullOrWhiteSpace(AfterJson) ||
            !string.IsNullOrWhiteSpace(MetadataJson);
    }
}

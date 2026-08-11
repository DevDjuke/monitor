using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure.Auth;

namespace Monitor.Infrastructure;

public sealed class MonitorDbContext(DbContextOptions<MonitorDbContext> options)
    : IdentityDbContext<MonitorUser>(options)
{
    public DbSet<MonitoredComponent> Components => Set<MonitoredComponent>();
    public DbSet<ComponentIngestionCredential> ComponentIngestionCredentials => Set<ComponentIngestionCredential>();
    public DbSet<ComponentCommand> ComponentCommands => Set<ComponentCommand>();
    public DbSet<AgentRun> Runs => Set<AgentRun>();
    public DbSet<TraceSpan> Spans => Set<TraceSpan>();
    public DbSet<LogEvent> LogEvents => Set<LogEvent>();
    public DbSet<RunAggregate> RunAggregates => Set<RunAggregate>();
    public DbSet<FailureGroup> FailureGroups => Set<FailureGroup>();
    public DbSet<FailureAlertRule> FailureAlertRules => Set<FailureAlertRule>();
    public DbSet<FailureAlertRuleDestination> FailureAlertRuleDestinations => Set<FailureAlertRuleDestination>();
    public DbSet<FailureAlertEvent> FailureAlertEvents => Set<FailureAlertEvent>();
    public DbSet<AlertDeliveryDestination> AlertDeliveryDestinations => Set<AlertDeliveryDestination>();
    public DbSet<AlertDelivery> AlertDeliveries => Set<AlertDelivery>();
    public DbSet<UsageBudget> UsageBudgets => Set<UsageBudget>();
    public DbSet<UsageBudgetDestination> UsageBudgetDestinations => Set<UsageBudgetDestination>();
    public DbSet<UsageBudgetAlertEvent> UsageBudgetAlertEvents => Set<UsageBudgetAlertEvent>();
    public DbSet<UsageBudgetAlertDelivery> UsageBudgetAlertDeliveries => Set<UsageBudgetAlertDelivery>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<SavedView> SavedViews => Set<SavedView>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasSequence<long>("RunSequence");

        var component = modelBuilder.Entity<MonitoredComponent>();
        component.ToTable("Components");
        component.HasKey(x => x.Id);
        component.Property(x => x.Name).HasMaxLength(200);
        component.Property(x => x.Slug).HasMaxLength(120);
        component.Property(x => x.Environment).HasMaxLength(80);
        component.Property(x => x.Version).HasMaxLength(80);
        component.Property(x => x.ControlState).HasDefaultValue(ComponentControlState.Active);
        component.HasIndex(x => new { x.Slug, x.Environment }).IsUnique();

        var componentCommand = modelBuilder.Entity<ComponentCommand>();
        componentCommand.ToTable("ComponentCommands");
        componentCommand.HasKey(x => x.Id);
        componentCommand.Property(x => x.RequestedBy).HasMaxLength(256);
        componentCommand.Property(x => x.Error).HasMaxLength(4000);
        componentCommand.HasIndex(x => new { x.ComponentId, x.Status, x.AvailableAt, x.CreatedAt });
        componentCommand.HasIndex(x => x.LeaseExpiresAt);
        componentCommand.HasIndex(x => new { x.Status, x.ExpiresAt });
        componentCommand.HasIndex(x => x.TargetRunId);
        componentCommand.HasIndex(x => x.CreatedAt).IsDescending();
        componentCommand.HasOne(x => x.Component)
            .WithMany(x => x.Commands)
            .HasForeignKey(x => x.ComponentId)
            .OnDelete(DeleteBehavior.Restrict);

        var ingestionCredential = modelBuilder.Entity<ComponentIngestionCredential>();
        ingestionCredential.ToTable("ComponentIngestionCredentials");
        ingestionCredential.HasKey(x => x.Id);
        ingestionCredential.Property(x => x.Name).HasMaxLength(200);
        ingestionCredential.Property(x => x.KeyId).HasMaxLength(64);
        ingestionCredential.Property(x => x.KeyHash).HasMaxLength(32);
        ingestionCredential.Property(x => x.CreatedBy).HasMaxLength(256);
        ingestionCredential.Property(x => x.RevokedBy).HasMaxLength(256);
        ingestionCredential.HasIndex(x => x.KeyId).IsUnique();
        ingestionCredential.HasIndex(x => new { x.ComponentId, x.RevokedAt });
        ingestionCredential.HasIndex(x => x.LastUsedAt);
        ingestionCredential.HasOne(x => x.Component)
            .WithMany(x => x.IngestionCredentials)
            .HasForeignKey(x => x.ComponentId)
            .OnDelete(DeleteBehavior.Cascade);

        var failureGroup = modelBuilder.Entity<FailureGroup>();
        failureGroup.ToTable("FailureGroups");
        failureGroup.HasKey(x => x.Id);
        failureGroup.Property(x => x.Fingerprint).HasMaxLength(64);
        failureGroup.Property(x => x.FailureType).HasMaxLength(240);
        failureGroup.Property(x => x.Operation).HasMaxLength(240);
        failureGroup.Property(x => x.Dependency).HasMaxLength(240);
        failureGroup.Property(x => x.MessageTemplate).HasMaxLength(500);
        failureGroup.HasIndex(x => x.Fingerprint).IsUnique();
        failureGroup.HasIndex(x => new { x.Category, x.LastSeenAt });
        failureGroup.HasIndex(x => x.LastSeenAt);

        var alertRule = modelBuilder.Entity<FailureAlertRule>();
        alertRule.ToTable("FailureAlertRules");
        alertRule.HasKey(x => x.Id);
        alertRule.Property(x => x.Name).HasMaxLength(200);
        alertRule.Property(x => x.DeliverToAllEnabledDestinations).HasDefaultValue(true);
        alertRule.HasIndex(x => new { x.FailureGroupId, x.Enabled });
        alertRule.HasIndex(x => new { x.Enabled, x.LastEvaluatedAt });
        alertRule.HasIndex(x => new { x.IsDeleted, x.Enabled, x.LastEvaluatedAt });
        alertRule.HasOne(x => x.FailureGroup)
            .WithMany(x => x.AlertRules)
            .HasForeignKey(x => x.FailureGroupId)
            .OnDelete(DeleteBehavior.Restrict);

        var alertRuleDestination = modelBuilder.Entity<FailureAlertRuleDestination>();
        alertRuleDestination.ToTable("FailureAlertRuleDestinations");
        alertRuleDestination.HasKey(x => new { x.FailureAlertRuleId, x.DestinationId });
        alertRuleDestination.HasIndex(x => x.DestinationId);
        alertRuleDestination.HasOne(x => x.FailureAlertRule)
            .WithMany(x => x.DestinationAssignments)
            .HasForeignKey(x => x.FailureAlertRuleId)
            .OnDelete(DeleteBehavior.Cascade);
        alertRuleDestination.HasOne(x => x.Destination)
            .WithMany(x => x.AlertRuleAssignments)
            .HasForeignKey(x => x.DestinationId)
            .OnDelete(DeleteBehavior.Restrict);

        var alertEvent = modelBuilder.Entity<FailureAlertEvent>();
        alertEvent.ToTable("FailureAlertEvents");
        alertEvent.HasKey(x => x.Id);
        alertEvent.Property(x => x.AcknowledgedBy).HasMaxLength(256);
        alertEvent.HasIndex(x => x.TriggeredAt).IsDescending();
        alertEvent.HasIndex(x => x.AcknowledgedAt);
        alertEvent.HasIndex(x => new { x.FailureGroupId, x.TriggeredAt }).IsDescending(false, true);
        alertEvent.HasIndex(x => new { x.AlertRuleId, x.TriggeredAt }).IsDescending(false, true);
        alertEvent.HasOne(x => x.AlertRule)
            .WithMany(x => x.Events)
            .HasForeignKey(x => x.AlertRuleId)
            .OnDelete(DeleteBehavior.Restrict);
        alertEvent.HasOne(x => x.FailureGroup)
            .WithMany(x => x.AlertEvents)
            .HasForeignKey(x => x.FailureGroupId)
            .OnDelete(DeleteBehavior.Restrict);

        var destination = modelBuilder.Entity<AlertDeliveryDestination>();
        destination.ToTable("AlertDeliveryDestinations");
        destination.HasKey(x => x.Id);
        destination.Property(x => x.Name).HasMaxLength(200);
        destination.Property(x => x.EndpointUrl).HasMaxLength(2000);
        destination.Property(x => x.ProtectedSecret).HasMaxLength(4000);
        destination.Property(x => x.LastFailure).HasMaxLength(2000);
        destination.HasIndex(x => new { x.Enabled, x.Kind });
        destination.HasIndex(x => x.Name);

        var delivery = modelBuilder.Entity<AlertDelivery>();
        delivery.ToTable("AlertDeliveries");
        delivery.HasKey(x => x.Id);
        delivery.Property(x => x.LastError).HasMaxLength(4000);
        delivery.HasIndex(x => new { x.Status, x.NextAttemptAt });
        delivery.HasIndex(x => new { x.AlertEventId, x.DestinationId }).IsUnique();
        delivery.HasIndex(x => new { x.DestinationId, x.CreatedAt }).IsDescending(false, true);
        delivery.HasOne(x => x.AlertEvent)
            .WithMany(x => x.Deliveries)
            .HasForeignKey(x => x.AlertEventId)
            .OnDelete(DeleteBehavior.Restrict);
        delivery.HasOne(x => x.Destination)
            .WithMany(x => x.Deliveries)
            .HasForeignKey(x => x.DestinationId)
            .OnDelete(DeleteBehavior.Restrict);

        var budget = modelBuilder.Entity<UsageBudget>();
        budget.ToTable("UsageBudgets");
        budget.HasKey(x => x.Id);
        budget.Property(x => x.Name).HasMaxLength(200);
        budget.Property(x => x.Environment).HasMaxLength(80);
        budget.Property(x => x.Model).HasMaxLength(160);
        budget.Property(x => x.DeliverToAllEnabledDestinations).HasDefaultValue(true);
        budget.HasIndex(x => new { x.IsDeleted, x.Enabled, x.LastEvaluatedAt });
        budget.HasIndex(x => new { x.ComponentId, x.Environment, x.Model, x.Period });
        budget.HasOne(x => x.Component)
            .WithMany()
            .HasForeignKey(x => x.ComponentId)
            .OnDelete(DeleteBehavior.Restrict);

        var budgetDestination = modelBuilder.Entity<UsageBudgetDestination>();
        budgetDestination.ToTable("UsageBudgetDestinations");
        budgetDestination.HasKey(x => new { x.UsageBudgetId, x.DestinationId });
        budgetDestination.HasIndex(x => x.DestinationId);
        budgetDestination.HasOne(x => x.UsageBudget)
            .WithMany(x => x.DestinationAssignments)
            .HasForeignKey(x => x.UsageBudgetId)
            .OnDelete(DeleteBehavior.Cascade);
        budgetDestination.HasOne(x => x.Destination)
            .WithMany()
            .HasForeignKey(x => x.DestinationId)
            .OnDelete(DeleteBehavior.Restrict);

        var budgetAlert = modelBuilder.Entity<UsageBudgetAlertEvent>();
        budgetAlert.ToTable("UsageBudgetAlertEvents");
        budgetAlert.HasKey(x => x.Id);
        budgetAlert.Property(x => x.AcknowledgedBy).HasMaxLength(256);
        budgetAlert.HasIndex(x => x.TriggeredAt).IsDescending();
        budgetAlert.HasIndex(x => x.AcknowledgedAt);
        budgetAlert.HasIndex(x => new { x.UsageBudgetId, x.PeriodStart, x.Level });
        budgetAlert.HasOne(x => x.UsageBudget)
            .WithMany(x => x.AlertEvents)
            .HasForeignKey(x => x.UsageBudgetId)
            .OnDelete(DeleteBehavior.Restrict);

        var budgetDelivery = modelBuilder.Entity<UsageBudgetAlertDelivery>();
        budgetDelivery.ToTable("UsageBudgetAlertDeliveries");
        budgetDelivery.HasKey(x => x.Id);
        budgetDelivery.Property(x => x.LastError).HasMaxLength(4000);
        budgetDelivery.HasIndex(x => new { x.Status, x.NextAttemptAt });
        budgetDelivery.HasIndex(x => new { x.BudgetAlertEventId, x.DestinationId }).IsUnique();
        budgetDelivery.HasIndex(x => new { x.DestinationId, x.CreatedAt }).IsDescending(false, true);
        budgetDelivery.HasOne(x => x.BudgetAlertEvent)
            .WithMany(x => x.Deliveries)
            .HasForeignKey(x => x.BudgetAlertEventId)
            .OnDelete(DeleteBehavior.Restrict);
        budgetDelivery.HasOne(x => x.Destination)
            .WithMany()
            .HasForeignKey(x => x.DestinationId)
            .OnDelete(DeleteBehavior.Restrict);

        var auditEvent = modelBuilder.Entity<AuditEvent>();
        auditEvent.ToTable("AuditEvents");
        auditEvent.HasKey(x => x.Id);
        auditEvent.Property(x => x.ActorId).HasMaxLength(450);
        auditEvent.Property(x => x.ActorName).HasMaxLength(256);
        auditEvent.Property(x => x.Action).HasMaxLength(120);
        auditEvent.Property(x => x.TargetType).HasMaxLength(120);
        auditEvent.Property(x => x.TargetId).HasMaxLength(200);
        auditEvent.Property(x => x.TargetName).HasMaxLength(240);
        auditEvent.HasIndex(x => x.OccurredAt).IsDescending();
        auditEvent.HasIndex(x => new { x.ActorType, x.OccurredAt }).IsDescending(false, true);
        auditEvent.HasIndex(x => new { x.Action, x.OccurredAt }).IsDescending(false, true);
        auditEvent.HasIndex(x => new { x.TargetType, x.OccurredAt }).IsDescending(false, true);
        auditEvent.HasIndex(x => new { x.TargetType, x.TargetId, x.OccurredAt }).IsDescending(false, false, true);

        var savedView = modelBuilder.Entity<SavedView>();
        savedView.ToTable("SavedViews");
        savedView.HasKey(x => x.Id);
        savedView.Property(x => x.UserId).HasMaxLength(450);
        savedView.Property(x => x.Name).HasMaxLength(120);
        savedView.Property(x => x.NameKey).HasMaxLength(120);
        savedView.Property(x => x.QueryString).HasMaxLength(4000);
        savedView.HasIndex(x => new { x.UserId, x.Surface, x.NameKey }).IsUnique();
        savedView.HasIndex(x => new { x.UserId, x.IsPinned, x.UpdatedAt }).IsDescending(false, false, true);
        savedView.HasOne<MonitorUser>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        var run = modelBuilder.Entity<AgentRun>();
        run.ToTable("Runs");
        run.HasKey(x => x.Id);
        run.Property(x => x.Sequence)
            .HasDefaultValueSql("NEXT VALUE FOR [RunSequence]");
        run.Property(x => x.Name).HasMaxLength(240);
        run.Property(x => x.ExternalId).HasMaxLength(200);
        run.Property(x => x.TraceId).HasMaxLength(32);
        run.Property(x => x.Trigger).HasMaxLength(120);
        run.Property(x => x.Model).HasMaxLength(160);
        run.HasIndex(x => x.Sequence).IsUnique().IsDescending();
        run.HasIndex(x => x.StartedAt);
        run.HasIndex(x => x.AggregatedAt);
        run.HasIndex(x => new { x.ComponentId, x.ExternalId });
        run.HasIndex(x => new { x.ComponentId, x.TraceId });
        run.HasIndex(x => x.FailureGroupId);
        run.HasIndex(x => new { x.FailureGroupId, x.CompletedAt, x.Sequence });
        run.HasIndex(x => new { x.Status, x.CompletedAt, x.AggregatedAt });
        run.HasOne(x => x.Component)
            .WithMany(x => x.Runs)
            .HasForeignKey(x => x.ComponentId)
            .OnDelete(DeleteBehavior.Cascade);
        run.HasOne(x => x.FailureGroup)
            .WithMany(x => x.Runs)
            .HasForeignKey(x => x.FailureGroupId)
            .OnDelete(DeleteBehavior.Restrict);

        var span = modelBuilder.Entity<TraceSpan>();
        span.ToTable("Spans");
        span.HasKey(x => x.Id);
        span.Property(x => x.Name).HasMaxLength(240);
        span.Property(x => x.ExternalSpanId).HasMaxLength(16);
        span.Property(x => x.ExternalParentSpanId).HasMaxLength(16);
        span.Property(x => x.ErrorType).HasMaxLength(240);
        span.Property(x => x.Model).HasMaxLength(160);
        span.HasIndex(x => new { x.RunId, x.StartedAt });
        span.HasIndex(x => new { x.RunId, x.ExternalSpanId });
        span.HasOne(x => x.Run)
            .WithMany(x => x.Spans)
            .HasForeignKey(x => x.RunId)
            .OnDelete(DeleteBehavior.Cascade);

        var logEvent = modelBuilder.Entity<LogEvent>();
        logEvent.ToTable("LogEvents");
        logEvent.HasKey(x => x.Id);
        logEvent.Property(x => x.ExternalTraceId).HasMaxLength(32);
        logEvent.Property(x => x.ExternalSpanId).HasMaxLength(16);
        logEvent.Property(x => x.ExternalRecordId).HasMaxLength(200);
        logEvent.Property(x => x.DedupeKey).HasMaxLength(64);
        logEvent.Property(x => x.SeverityText).HasMaxLength(80);
        logEvent.Property(x => x.EventName).HasMaxLength(256);
        logEvent.Property(x => x.Message).HasMaxLength(4000);
        logEvent.Property(x => x.MessageTemplate).HasMaxLength(4000);
        logEvent.Property(x => x.ExceptionType).HasMaxLength(240);
        logEvent.Property(x => x.ExceptionMessage).HasMaxLength(4000);
        logEvent.Property(x => x.Source).HasMaxLength(240);
        logEvent.HasIndex(x => x.Timestamp).IsDescending();
        logEvent.HasIndex(x => new { x.ComponentId, x.Timestamp }).IsDescending(false, true);
        logEvent.HasIndex(x => new { x.RunId, x.Timestamp }).IsDescending(false, true);
        logEvent.HasIndex(x => new { x.SpanId, x.Timestamp }).IsDescending(false, true);
        logEvent.HasIndex(x => x.DedupeKey);
        logEvent.HasIndex(x => x.ExternalRecordId);
        logEvent.HasOne(x => x.Component)
            .WithMany(x => x.LogEvents)
            .HasForeignKey(x => x.ComponentId)
            .OnDelete(DeleteBehavior.NoAction);
        logEvent.HasOne(x => x.Run)
            .WithMany(x => x.LogEvents)
            .HasForeignKey(x => x.RunId)
            .OnDelete(DeleteBehavior.Cascade);
        logEvent.HasOne(x => x.Span)
            .WithMany(x => x.LogEvents)
            .HasForeignKey(x => x.SpanId)
            .OnDelete(DeleteBehavior.NoAction);

        var aggregate = modelBuilder.Entity<RunAggregate>();
        aggregate.ToTable("RunAggregates");
        aggregate.HasKey(x => x.Id);
        aggregate.Property(x => x.ComponentName).HasMaxLength(200);
        aggregate.Property(x => x.Environment).HasMaxLength(80);
        aggregate.Property(x => x.Model).HasMaxLength(160);
        aggregate.HasIndex(x => new { x.BucketStart, x.ComponentId, x.Model }).IsUnique();
        aggregate.HasIndex(x => x.BucketStart);
        aggregate.HasIndex(x => new { x.ComponentId, x.BucketStart });
    }
}

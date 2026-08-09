using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure.Auth;

namespace Monitor.Infrastructure;

public sealed class MonitorDbContext(DbContextOptions<MonitorDbContext> options)
    : IdentityDbContext<MonitorUser>(options)
{
    public DbSet<MonitoredComponent> Components => Set<MonitoredComponent>();
    public DbSet<AgentRun> Runs => Set<AgentRun>();
    public DbSet<TraceSpan> Spans => Set<TraceSpan>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var component = modelBuilder.Entity<MonitoredComponent>();
        component.ToTable("Components");
        component.HasKey(x => x.Id);
        component.Property(x => x.Name).HasMaxLength(200);
        component.Property(x => x.Slug).HasMaxLength(120);
        component.Property(x => x.Environment).HasMaxLength(80);
        component.Property(x => x.Version).HasMaxLength(80);
        component.HasIndex(x => new { x.Slug, x.Environment }).IsUnique();

        var run = modelBuilder.Entity<AgentRun>();
        run.ToTable("Runs");
        run.HasKey(x => x.Id);
        run.Property(x => x.Name).HasMaxLength(240);
        run.Property(x => x.ExternalId).HasMaxLength(200);
        run.Property(x => x.Trigger).HasMaxLength(120);
        run.Property(x => x.Model).HasMaxLength(160);
        run.HasIndex(x => x.StartedAt);
        run.HasIndex(x => new { x.ComponentId, x.ExternalId });
        run.HasOne(x => x.Component)
            .WithMany(x => x.Runs)
            .HasForeignKey(x => x.ComponentId)
            .OnDelete(DeleteBehavior.Cascade);

        var span = modelBuilder.Entity<TraceSpan>();
        span.ToTable("Spans");
        span.HasKey(x => x.Id);
        span.Property(x => x.Name).HasMaxLength(240);
        span.HasIndex(x => new { x.RunId, x.StartedAt });
        span.HasOne(x => x.Run)
            .WithMany(x => x.Spans)
            .HasForeignKey(x => x.RunId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

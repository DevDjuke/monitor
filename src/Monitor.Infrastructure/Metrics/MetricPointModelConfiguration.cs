using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Monitor.Domain;

namespace Monitor.Infrastructure.Metrics;

public static class MetricPointModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        var metric = modelBuilder.Entity<MetricPoint>();
        metric.ToTable("MetricPoints");
        metric.HasKey(x => x.Id);
        metric.Property(x => x.Name).HasMaxLength(240);
        metric.Property(x => x.Description).HasMaxLength(1000);
        metric.Property(x => x.Unit).HasMaxLength(80);
        metric.Property(x => x.ScopeName).HasMaxLength(240);
        metric.Property(x => x.ScopeVersion).HasMaxLength(80);
        metric.Property(x => x.ResourceSchemaUrl).HasMaxLength(500);
        metric.Property(x => x.ScopeSchemaUrl).HasMaxLength(500);
        metric.Property(x => x.Source).HasMaxLength(40);
        metric.Property(x => x.DedupeKey).HasMaxLength(64);
        metric.Property(x => x.Count).HasPrecision(20, 0);
        metric.Property(x => x.ZeroCount).HasPrecision(20, 0);
        metric.HasIndex(x => x.DedupeKey).IsUnique();
        metric.HasIndex(x => x.Timestamp).IsDescending();
        metric.HasIndex(x => new { x.ComponentId, x.Timestamp }).IsDescending(false, true);
        metric.HasIndex(x => new { x.Name, x.Timestamp }).IsDescending(false, true);
        metric.HasIndex(x => new { x.Kind, x.Timestamp }).IsDescending(false, true);
        metric.HasIndex(x => new { x.ComponentId, x.Name, x.Timestamp }).IsDescending(false, false, true);
        metric.HasOne(x => x.Component)
            .WithMany()
            .HasForeignKey(x => x.ComponentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class MonitorModelCustomizer(ModelCustomizerDependencies dependencies)
    : ModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);
        if (context is MonitorDbContext)
        {
            MetricPointModelConfiguration.Configure(modelBuilder);
        }
    }
}

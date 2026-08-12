using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Monitor.Infrastructure;

namespace Monitor.Web.Production;

public sealed class MonitorDatabaseReadinessHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MonitorDbContext>();

            if (!await db.Database.CanConnectAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy("SQL Server is not reachable.");
            }

            var pendingMigrations = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();
            if (pendingMigrations.Length > 0)
            {
                return HealthCheckResult.Unhealthy(
                    $"Database schema has {pendingMigrations.Length} pending migration(s).",
                    data: new Dictionary<string, object>
                    {
                        ["pendingMigrations"] = pendingMigrations
                    });
            }

            return HealthCheckResult.Healthy("SQL Server is reachable and the schema is current.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database readiness check failed.", ex);
        }
    }
}

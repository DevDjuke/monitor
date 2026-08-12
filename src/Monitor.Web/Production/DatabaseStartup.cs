using Microsoft.EntityFrameworkCore;
using Monitor.Infrastructure;
using Monitor.Web.Auth;

namespace Monitor.Web.Production;

public static class DatabaseStartup
{
    public static async Task InitializeAsync(
        IServiceProvider services,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ProductionOptions options,
        bool migrateOnly,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitorDbContext>();

        if (migrateOnly || options.MigrateOnStartup)
        {
            await db.Database.MigrateAsync(cancellationToken);
        }
        else
        {
            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();
            if (pending.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Monitor database has {pending.Length} pending migration(s), but Production:MigrateOnStartup is false. " +
                    "Run 'dotnet Monitor.Web.dll --migrate-only' for this release, or explicitly enable migration-on-startup for a single-node deployment.");
            }
        }

        if (!migrateOnly)
        {
            await AuthBootstrapper.EnsureBootstrapAdminAsync(scope.ServiceProvider, configuration, environment);
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Monitor.Infrastructure;

public sealed class MonitorDbContextFactory : IDesignTimeDbContextFactory<MonitorDbContext>
{
    public MonitorDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MonitorDbContext>()
            .UseSqlite("Data Source=monitor.db")
            .Options;

        return new MonitorDbContext(options);
    }
}

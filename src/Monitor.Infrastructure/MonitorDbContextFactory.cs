using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Monitor.Infrastructure;

public sealed class MonitorDbContextFactory : IDesignTimeDbContextFactory<MonitorDbContext>
{
    public MonitorDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MonitorDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=Monitor;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True")
            .Options;

        return new MonitorDbContext(options);
    }
}

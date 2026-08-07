using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Monitor.Infrastructure;
using Monitor.Web.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var connectionString = builder.Configuration.GetConnectionString("Monitor")
    ?? "Data Source=monitor.db";

builder.Services.AddDbContext<MonitorDbContext>(options =>
    options.UseSqlite(connectionString));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.MapRazorPages();
app.MapMonitoringApi();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MonitorDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.Run();

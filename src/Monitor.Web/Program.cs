using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Monitor.Infrastructure;
using Monitor.Infrastructure.Auth;
using Monitor.Web.Api;
using Monitor.Web.Auth;
using Monitor.Web.Realtime;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Account/Setup");
    options.Conventions.AllowAnonymousToPage("/Error");
});
builder.Services.AddSignalR();

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var connectionString = builder.Configuration.GetConnectionString("Monitor")
    ?? "Server=(localdb)\\MSSQLLocalDB;Database=Monitor;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True";

builder.Services.AddDbContext<MonitorDbContext>(options =>
    options.UseSqlServer(connectionString));

builder.Services
    .AddIdentity<MonitorUser, IdentityRole>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 12;
        options.Password.RequiredUniqueChars = 4;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = false;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
    })
    .AddEntityFrameworkStores<MonitorDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "Monitor.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.LoginPath = "/account/login";
    options.AccessDeniedPath = "/account/login";
    options.SlidingExpiration = true;
    options.ExpireTimeSpan = TimeSpan.FromHours(12);

    options.Events.OnRedirectToLogin = context =>
    {
        if (IsMachineEndpoint(context.Request.Path))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };

    options.Events.OnRedirectToAccessDenied = context =>
    {
        if (IsMachineEndpoint(context.Request.Path))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();
app.MapHub<MonitorHub>("/hubs/monitor").RequireAuthorization();
app.MapMonitoringApi();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MonitorDbContext>();
    await db.Database.MigrateAsync();
    await AuthBootstrapper.EnsureBootstrapAdminAsync(scope.ServiceProvider, builder.Configuration, app.Environment);
}

app.Run();

static bool IsMachineEndpoint(PathString path) =>
    path.StartsWithSegments("/api") || path.StartsWithSegments("/hubs");

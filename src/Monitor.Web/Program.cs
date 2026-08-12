using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Monitor.Infrastructure;
using Monitor.Infrastructure.Auditing;
using Monitor.Infrastructure.Auth;
using Monitor.Infrastructure.Control;
using Monitor.Infrastructure.Failures;
using Monitor.Infrastructure.Logs;
using Monitor.Infrastructure.Retention;
using Monitor.Infrastructure.Usage;
using Monitor.Web.Api;
using Monitor.Web.Auth;
using Monitor.Web.Otlp;
using Monitor.Web.Production;
using Monitor.Web.Realtime;
using Monitor.Web.Services;

var migrateOnly = args.Any(x => string.Equals(x, "--migrate-only", StringComparison.OrdinalIgnoreCase));
var builderArgs = args
    .Where(x => !string.Equals(x, "--migrate-only", StringComparison.OrdinalIgnoreCase))
    .ToArray();

var builder = WebApplication.CreateBuilder(builderArgs);
ProductionSecretLoader.Load(builder.Configuration);

var configuredConnectionString = builder.Configuration.GetConnectionString("Monitor");
var connectionString = string.IsNullOrWhiteSpace(configuredConnectionString)
    ? builder.Environment.IsProduction()
        ? string.Empty
        : "Server=(localdb)\\MSSQLLocalDB;Database=Monitor;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True"
    : configuredConnectionString;

var productionOptions = ProductionConfigurationValidator.BindAndValidate(
    builder.Configuration,
    builder.Environment,
    connectionString,
    migrateOnly);

builder.Services.AddSingleton(productionOptions);
builder.Services.AddScoped<RoleAuthorizationPageFilter>();
builder.Services
    .AddRazorPages(options =>
    {
        options.Conventions.AuthorizeFolder("/", MonitorPolicies.View);
        options.Conventions.AuthorizePage("/Audit", MonitorPolicies.Audit);
        options.Conventions.AuthorizePage("/AlertRuleEdit", MonitorPolicies.Configure);
        options.Conventions.AuthorizePage("/BudgetEdit", MonitorPolicies.Configure);
        options.Conventions.AuthorizePage("/Operators", MonitorPolicies.ManageOperators);
        options.Conventions.AllowAnonymousToPage("/Account/Login");
        options.Conventions.AllowAnonymousToPage("/Account/Setup");
        options.Conventions.AllowAnonymousToPage("/Account/AccessDenied");
        options.Conventions.AllowAnonymousToPage("/Error");
    })
    .AddMvcOptions(options => options.Filters.AddService<RoleAuthorizationPageFilter>());
builder.Services.AddSignalR();

var dataProtection = builder.Services
    .AddDataProtection()
    .SetApplicationName(productionOptions.DataProtectionApplicationName);

if (!migrateOnly && !string.IsNullOrWhiteSpace(productionOptions.DataProtectionKeyPath))
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(productionOptions.DataProtectionKeyPath));
}

if (productionOptions.ForwardedHeaders.Enabled)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor |
            ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.RequireHeaderSymmetry = true;

        foreach (var proxy in productionOptions.ForwardedHeaders.KnownProxies)
        {
            options.KnownProxies.Add(IPAddress.Parse(proxy));
        }
    });
}

builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy("Monitor process is running."), tags: new[] { "live" })
    .AddCheck<MonitorDatabaseReadinessHealthCheck>("database", tags: new[] { "ready" });

builder.Services.AddHttpClient("alert-webhooks")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient("alert-adapters")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddScoped<MonitorRealtimeSaveChangesInterceptor>();
builder.Services.AddDbContext<MonitorDbContext>((services, options) =>
    options
        .UseSqlServer(connectionString)
        .AddInterceptors(services.GetRequiredService<MonitorRealtimeSaveChangesInterceptor>()));

builder.Services.AddScoped<AuditTrailWriter>();
builder.Services.AddScoped<MonitorRealtimePublisher>();
builder.Services.AddSingleton<SavedViewQueryPolicy>();
builder.Services.Configure<RetentionOptions>(builder.Configuration.GetSection(RetentionOptions.SectionName));
builder.Services.AddScoped<RetentionAggregationService>();
builder.Services.AddHostedService<RetentionWorker>();
builder.Services.AddSingleton<FailureClassifier>();
builder.Services.AddScoped<FailureGroupingService>();
builder.Services.AddHostedService<FailureGroupingWorker>();
builder.Services.Configure<FailureAlertingOptions>(builder.Configuration.GetSection(FailureAlertingOptions.SectionName));
builder.Services.AddScoped<FailureAlertEvaluationService>();
builder.Services.AddHostedService<FailureAlertingWorker>();
builder.Services.Configure<UsageBudgetOptions>(builder.Configuration.GetSection(UsageBudgetOptions.SectionName));
builder.Services.AddScoped<UsageBudgetEvaluationService>();
builder.Services.AddHostedService<UsageBudgetWorker>();
builder.Services.Configure<ComponentCommandOptions>(builder.Configuration.GetSection(ComponentCommandOptions.SectionName));
builder.Services.AddScoped<ComponentCommandService>();
builder.Services.AddHostedService<ComponentCommandExpiryWorker>();
builder.Services.Configure<AlertDeliveryOptions>(builder.Configuration.GetSection(AlertDeliveryOptions.SectionName));
builder.Services.AddScoped<AlertDestinationSecretProtector>();
builder.Services.AddScoped<WebhookAlertSender>();
builder.Services.AddScoped<AlertDeliverySender>();
builder.Services.AddHostedService<AlertDeliveryWorker>();
builder.Services.AddScoped<ComponentCredentialIssuer>();
builder.Services.AddScoped<IngestionCredentialAuthenticator>();
builder.Services.AddScoped<LogCorrelationService>();
builder.Services.AddHostedService<LogCorrelationWorker>();
builder.Services.AddScoped<OtlpComponentScopeValidator>();
builder.Services.AddScoped<OtlpTraceImporter>();
builder.Services.AddScoped<OtlpLogImporter>();

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

builder.Services.AddAuthorization(MonitorPolicies.Configure);

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "Monitor.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsProduction()
        ? CookieSecurePolicy.Always
        : CookieSecurePolicy.SameAsRequest;
    options.LoginPath = "/account/login";
    options.AccessDeniedPath = "/account/access-denied";
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

await DatabaseStartup.InitializeAsync(
    app.Services,
    builder.Configuration,
    app.Environment,
    productionOptions,
    migrateOnly);

if (migrateOnly)
{
    return;
}

if (productionOptions.ForwardedHeaders.Enabled)
{
    app.UseForwardedHeaders();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

if (productionOptions.UseHttpsRedirection)
{
    app.UseWhen(
        context => !context.Request.Path.StartsWithSegments("/health"),
        branch => branch.UseHttpsRedirection());
}

app.UseStaticFiles();
app.UseRouting();
app.UseMiddleware<ComponentControlMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live")
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
}).AllowAnonymous();

app.MapRazorPages();
app.MapHub<MonitorHub>("/hubs/monitor").RequireAuthorization(MonitorPolicies.View);
app.MapMonitoringApi();
app.MapControlCommandApi();
app.MapLogApi();
app.MapOtlp();

app.Run();

static bool IsMachineEndpoint(PathString path) =>
    path.StartsWithSegments("/api") ||
    path.StartsWithSegments("/hubs") ||
    path.StartsWithSegments("/v1") ||
    path.StartsWithSegments("/health");

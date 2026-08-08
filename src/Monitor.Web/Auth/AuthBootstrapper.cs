using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Monitor.Infrastructure.Auth;

namespace Monitor.Web.Auth;

public static class AuthBootstrapper
{
    public static async Task EnsureBootstrapAdminAsync(
        IServiceProvider services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        var userManager = services.GetRequiredService<UserManager<MonitorUser>>();
        if (await userManager.Users.AnyAsync())
        {
            return;
        }

        var email = configuration["Monitor:BootstrapAdmin:Email"]?.Trim();
        var password = configuration["Monitor:BootstrapAdmin:Password"];

        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(password))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    "Monitor has no administrator account. Configure Monitor__BootstrapAdmin__Email and Monitor__BootstrapAdmin__Password before starting in Production.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "Both Monitor__BootstrapAdmin__Email and Monitor__BootstrapAdmin__Password must be configured together.");
        }

        var user = new MonitorUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true
        };

        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(x => x.Description));
            throw new InvalidOperationException($"Unable to create the bootstrap administrator: {errors}");
        }
    }
}

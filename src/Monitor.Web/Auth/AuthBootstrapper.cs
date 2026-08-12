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
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();

        await EnsureRolesAsync(roleManager);

        var existingUsers = await userManager.Users.ToListAsync();
        if (existingUsers.Count > 0)
        {
            // P12 upgrade path: accounts created before roles existed had unrestricted access.
            // Promote any account without a recognized Monitor role to Owner so the rollout
            // cannot lock an existing administrator out.
            foreach (var existingUser in existingUsers)
            {
                var roles = await userManager.GetRolesAsync(existingUser);
                if (!roles.Any(MonitorRoles.All.Contains))
                {
                    var roleResult = await userManager.AddToRoleAsync(existingUser, MonitorRoles.Owner);
                    ThrowIfFailed(roleResult, $"Unable to assign Owner to existing account {existingUser.Email}");
                }
            }

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
        ThrowIfFailed(result, "Unable to create the bootstrap administrator");

        var ownerResult = await userManager.AddToRoleAsync(user, MonitorRoles.Owner);
        ThrowIfFailed(ownerResult, "Unable to assign Owner to the bootstrap administrator");
    }

    private static async Task EnsureRolesAsync(RoleManager<IdentityRole> roleManager)
    {
        foreach (var role in MonitorRoles.All)
        {
            if (await roleManager.RoleExistsAsync(role))
            {
                continue;
            }

            var result = await roleManager.CreateAsync(new IdentityRole(role));
            ThrowIfFailed(result, $"Unable to create Monitor role {role}");
        }
    }

    private static void ThrowIfFailed(IdentityResult result, string message)
    {
        if (result.Succeeded)
        {
            return;
        }

        var errors = string.Join("; ", result.Errors.Select(x => x.Description));
        throw new InvalidOperationException($"{message}: {errors}");
    }
}

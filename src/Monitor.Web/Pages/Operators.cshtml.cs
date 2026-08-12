using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure;
using Monitor.Infrastructure.Auditing;
using Monitor.Infrastructure.Auth;
using Monitor.Web.Auth;

namespace Monitor.Web.Pages;

public sealed class OperatorsModel(
    UserManager<MonitorUser> userManager,
    MonitorDbContext db,
    AuditTrailWriter audit) : PageModel
{
    public IReadOnlyList<OperatorRow> Operators { get; private set; } = [];
    public IReadOnlyList<string> AvailableRoles => MonitorRoles.All;

    [BindProperty]
    public CreateOperatorInput Input { get; set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        if (!MonitorRoles.All.Contains(Input.Role))
        {
            ModelState.AddModelError(nameof(Input.Role), "Select a valid Monitor role.");
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync(cancellationToken);
            return Page();
        }

        var email = Input.Email.Trim();
        if (await userManager.FindByEmailAsync(email) is not null)
        {
            ModelState.AddModelError(nameof(Input.Email), "An account with that email already exists.");
            await LoadAsync(cancellationToken);
            return Page();
        }

        var user = new MonitorUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true
        };

        var createResult = await userManager.CreateAsync(user, Input.Password);
        if (!createResult.Succeeded)
        {
            AddErrors(createResult);
            await LoadAsync(cancellationToken);
            return Page();
        }

        var roleResult = await userManager.AddToRoleAsync(user, Input.Role);
        if (!roleResult.Succeeded)
        {
            await userManager.DeleteAsync(user);
            AddErrors(roleResult);
            await LoadAsync(cancellationToken);
            return Page();
        }

        audit.RecordOperator(
            User,
            AuditActions.OperatorAccountCreated,
            AuditTargetTypes.OperatorAccount,
            user.Id,
            email,
            after: new { email, role = Input.Role },
            occurredAt: DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);

        TempData["StatusMessage"] = $"Created {email} as {Input.Role}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostChangeRoleAsync(
        string userId,
        string role,
        CancellationToken cancellationToken)
    {
        if (!MonitorRoles.All.Contains(role))
        {
            return BadRequest("Unknown Monitor role.");
        }

        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return NotFound();
        }

        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var currentRoles = (await userManager.GetRolesAsync(user))
            .Where(MonitorRoles.All.Contains)
            .ToArray();
        var currentRole = currentRoles.FirstOrDefault();

        if (user.Id == currentUserId && role != MonitorRoles.Owner)
        {
            TempData["StatusMessage"] = "You cannot remove your own Owner role.";
            return RedirectToPage();
        }

        if (currentRoles.Contains(MonitorRoles.Owner) && role != MonitorRoles.Owner &&
            await IsLastOwnerAsync(user.Id))
        {
            TempData["StatusMessage"] = "Monitor must keep at least one Owner.";
            return RedirectToPage();
        }

        if (currentRoles.Length == 1 && currentRole == role)
        {
            TempData["StatusMessage"] = $"{user.Email} is already {role}.";
            return RedirectToPage();
        }

        if (!currentRoles.Contains(role))
        {
            var addResult = await userManager.AddToRoleAsync(user, role);
            if (!addResult.Succeeded)
            {
                TempData["StatusMessage"] = FormatErrors(addResult);
                return RedirectToPage();
            }
        }

        var rolesToRemove = currentRoles.Where(x => x != role).ToArray();
        if (rolesToRemove.Length > 0)
        {
            var removeResult = await userManager.RemoveFromRolesAsync(user, rolesToRemove);
            if (!removeResult.Succeeded)
            {
                TempData["StatusMessage"] = FormatErrors(removeResult);
                return RedirectToPage();
            }
        }

        var stampResult = await userManager.UpdateSecurityStampAsync(user);
        if (!stampResult.Succeeded)
        {
            TempData["StatusMessage"] = FormatErrors(stampResult);
            return RedirectToPage();
        }

        audit.RecordOperator(
            User,
            AuditActions.OperatorRoleChanged,
            AuditTargetTypes.OperatorAccount,
            user.Id,
            user.Email,
            before: new { role = currentRole },
            after: new { role },
            metadata: new { securityStampRotated = true },
            occurredAt: DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);

        TempData["StatusMessage"] = $"Changed {user.Email} to {role}. Existing sessions will be revalidated shortly.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResetPasswordAsync(
        string userId,
        string newPassword,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(newPassword))
        {
            TempData["StatusMessage"] = "A new password is required.";
            return RedirectToPage();
        }

        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return NotFound();
        }

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var result = await userManager.ResetPasswordAsync(user, token, newPassword);
        if (!result.Succeeded)
        {
            TempData["StatusMessage"] = FormatErrors(result);
            return RedirectToPage();
        }

        await userManager.UpdateSecurityStampAsync(user);
        audit.RecordOperator(
            User,
            AuditActions.OperatorPasswordReset,
            AuditTargetTypes.OperatorAccount,
            user.Id,
            user.Email,
            metadata: new { securityStampRotated = true },
            occurredAt: DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);

        TempData["StatusMessage"] = $"Reset the password for {user.Email}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string userId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return NotFound();
        }

        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (user.Id == currentUserId)
        {
            TempData["StatusMessage"] = "You cannot delete your own account.";
            return RedirectToPage();
        }

        var roles = (await userManager.GetRolesAsync(user))
            .Where(MonitorRoles.All.Contains)
            .ToArray();
        if (roles.Contains(MonitorRoles.Owner) && await IsLastOwnerAsync(user.Id))
        {
            TempData["StatusMessage"] = "Monitor must keep at least one Owner.";
            return RedirectToPage();
        }

        var email = user.Email;
        var result = await userManager.DeleteAsync(user);
        if (!result.Succeeded)
        {
            TempData["StatusMessage"] = FormatErrors(result);
            return RedirectToPage();
        }

        audit.RecordOperator(
            User,
            AuditActions.OperatorAccountDeleted,
            AuditTargetTypes.OperatorAccount,
            userId,
            email,
            before: new { email, roles },
            occurredAt: DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);

        TempData["StatusMessage"] = $"Deleted {email}.";
        return RedirectToPage();
    }

    private async Task<bool> IsLastOwnerAsync(string userId)
    {
        var owners = await userManager.GetUsersInRoleAsync(MonitorRoles.Owner);
        return owners.Count == 1 && owners[0].Id == userId;
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var users = await userManager.Users
            .AsNoTracking()
            .OrderBy(x => x.Email)
            .ToListAsync(cancellationToken);

        var rows = new List<OperatorRow>(users.Count);
        foreach (var user in users)
        {
            var roles = (await userManager.GetRolesAsync(user))
                .Where(MonitorRoles.All.Contains)
                .ToArray();
            rows.Add(new OperatorRow(
                user.Id,
                user.Email ?? user.UserName ?? user.Id,
                roles.FirstOrDefault() ?? "Unassigned",
                user.Id == User.FindFirstValue(ClaimTypes.NameIdentifier)));
        }

        Operators = rows;
    }

    private void AddErrors(IdentityResult result)
    {
        foreach (var error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, error.Description);
        }
    }

    private static string FormatErrors(IdentityResult result) =>
        string.Join("; ", result.Errors.Select(x => x.Description));

    public sealed class CreateOperatorInput
    {
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required, DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [Required]
        public string Role { get; set; } = MonitorRoles.Viewer;
    }

    public sealed record OperatorRow(string Id, string Email, string Role, bool IsCurrentUser);
}

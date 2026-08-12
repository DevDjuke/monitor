using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Monitor.Infrastructure.Auth;
using Monitor.Web.Auth;

namespace Monitor.Web.Pages.Account;

public sealed class SetupModel(
    UserManager<MonitorUser> userManager,
    SignInManager<MonitorUser> signInManager,
    IWebHostEnvironment environment) : PageModel
{
    [BindProperty]
    public SetupInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        if (!environment.IsDevelopment())
        {
            return NotFound();
        }

        if (await userManager.Users.AnyAsync())
        {
            return LocalRedirect("/");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!environment.IsDevelopment())
        {
            return NotFound();
        }

        if (await userManager.Users.AnyAsync())
        {
            return LocalRedirect("/");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var email = Input.Email.Trim();
        var user = new MonitorUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true
        };

        var result = await userManager.CreateAsync(user, Input.Password);
        if (!result.Succeeded)
        {
            AddErrors(result);
            return Page();
        }

        var roleResult = await userManager.AddToRoleAsync(user, MonitorRoles.Owner);
        if (!roleResult.Succeeded)
        {
            await userManager.DeleteAsync(user);
            AddErrors(roleResult);
            return Page();
        }

        await signInManager.SignInAsync(user, isPersistent: false);
        return LocalRedirect("/");
    }

    private void AddErrors(IdentityResult result)
    {
        foreach (var error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, error.Description);
        }
    }

    public sealed class SetupInput
    {
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required, DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [Required, DataType(DataType.Password), Compare(nameof(Password))]
        [Display(Name = "Confirm password")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}

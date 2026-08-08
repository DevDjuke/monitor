using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Monitor.Infrastructure.Auth;

namespace Monitor.Web.Pages.Account;

public sealed class LogoutModel(SignInManager<MonitorUser> signInManager) : PageModel
{
    public IActionResult OnGet() => LocalRedirect("/");

    public async Task<IActionResult> OnPostAsync()
    {
        await signInManager.SignOutAsync();
        return LocalRedirect("/account/login");
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Monitor.Web.Auth;

public sealed class RoleAuthorizationPageFilter(IAuthorizationService authorization) : IAsyncPageFilter
{
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(
        PageHandlerExecutingContext context,
        PageHandlerExecutionDelegate next)
    {
        if (!HttpMethods.IsPost(context.HttpContext.Request.Method))
        {
            await next();
            return;
        }

        var page = context.ActionDescriptor.ViewEnginePath ?? string.Empty;
        if (page.StartsWith("/Account/", StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }

        var handlerName = context.HandlerMethod?.MethodInfo.Name ?? string.Empty;
        var policy = ResolvePolicy(page, handlerName);
        var result = await authorization.AuthorizeAsync(context.HttpContext.User, policy);
        if (!result.Succeeded)
        {
            context.Result = new ForbidResult();
            return;
        }

        await next();
    }

    private static string ResolvePolicy(string page, string handlerName)
    {
        if (page.Equals("/SavedViews", StringComparison.OrdinalIgnoreCase))
        {
            return MonitorPolicies.View;
        }

        if (page.Equals("/ComponentDetail", StringComparison.OrdinalIgnoreCase))
        {
            return handlerName.Contains("IssueCommand", StringComparison.Ordinal) ||
                   handlerName.Contains("CancelCommand", StringComparison.Ordinal)
                ? MonitorPolicies.Control
                : MonitorPolicies.Configure;
        }

        if (page.Equals("/Commands", StringComparison.OrdinalIgnoreCase))
        {
            return MonitorPolicies.Control;
        }

        if (page.Equals("/Operators", StringComparison.OrdinalIgnoreCase))
        {
            return MonitorPolicies.ManageOperators;
        }

        // Fail closed for future state-changing Razor handlers. New POST handlers are
        // configuration-only until they are explicitly classified as Control or View.
        return MonitorPolicies.Configure;
    }
}

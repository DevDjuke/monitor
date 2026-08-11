using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure;
using Monitor.Web.Services;

namespace Monitor.Web.Components;

public sealed class SavedViewsViewComponent(
    MonitorDbContext db,
    SavedViewQueryPolicy policy,
    ITempDataDictionaryFactory tempDataFactory) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(SavedViewSurface surface)
    {
        var userId = HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Content(string.Empty);
        }

        var definition = policy.GetDefinition(surface);
        var currentQuery = policy.Canonicalize(surface, Request.QueryString.Value);
        var views = await db.SavedViews
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.Surface == surface)
            .OrderByDescending(x => x.IsPinned)
            .ThenBy(x => x.Name)
            .Select(x => new
            {
                x.Id,
                x.Name,
                x.QueryString,
                x.IsPinned
            })
            .ToListAsync(HttpContext.RequestAborted);

        var rows = views
            .Select(x => new SavedViewToolbarRow(
                x.Id,
                x.Name,
                policy.BuildUrl(surface, x.QueryString),
                x.IsPinned,
                string.Equals(x.QueryString, currentQuery, StringComparison.Ordinal)))
            .ToList();

        var active = rows.FirstOrDefault(x => x.IsActive);
        var tempData = tempDataFactory.GetTempData(HttpContext);
        var status = tempData[SavedViewTempData.StatusKey] as string;
        var statusIsError = tempData[SavedViewTempData.IsErrorKey] as bool? ?? false;

        return View(new SavedViewToolbarModel(
            surface,
            definition.DisplayName,
            currentQuery,
            $"{Request.Path}{Request.QueryString}",
            active?.Id,
            rows,
            status,
            statusIsError));
    }
}

public sealed class PinnedSavedViewsViewComponent(
    MonitorDbContext db,
    SavedViewQueryPolicy policy) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var userId = HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Content(string.Empty);
        }

        var views = await db.SavedViews
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.IsPinned)
            .OrderByDescending(x => x.UpdatedAt)
            .ThenBy(x => x.Name)
            .Take(6)
            .Select(x => new
            {
                x.Id,
                x.Surface,
                x.Name,
                x.QueryString
            })
            .ToListAsync(HttpContext.RequestAborted);

        var rows = views
            .Select(x => new PinnedSavedViewRow(
                x.Id,
                x.Surface,
                policy.GetDefinition(x.Surface).DisplayName,
                x.Name,
                policy.BuildUrl(x.Surface, x.QueryString)))
            .ToList();

        return View(rows);
    }
}

public static class SavedViewTempData
{
    public const string StatusKey = "SavedViewStatus";
    public const string IsErrorKey = "SavedViewStatusIsError";
}

public sealed record SavedViewToolbarModel(
    SavedViewSurface Surface,
    string SurfaceName,
    string CurrentQueryString,
    string ReturnUrl,
    Guid? ActiveViewId,
    IReadOnlyList<SavedViewToolbarRow> Views,
    string? StatusMessage,
    bool StatusIsError);

public sealed record SavedViewToolbarRow(
    Guid Id,
    string Name,
    string Url,
    bool IsPinned,
    bool IsActive);

public sealed record PinnedSavedViewRow(
    Guid Id,
    SavedViewSurface Surface,
    string SurfaceName,
    string Name,
    string Url);

using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure;
using Monitor.Web.Components;
using Monitor.Web.Services;

namespace Monitor.Web.Pages;

public sealed class SavedViewsModel(
    MonitorDbContext db,
    SavedViewQueryPolicy policy) : PageModel
{
    private const int MaxSavedViewsPerUser = 100;
    private const int MaxPinnedViewsPerUser = 6;

    public IReadOnlyList<SavedViewRow> Rows { get; private set; } = [];
    public IReadOnlyList<SavedViewSurfaceDefinition> Surfaces => policy.GetDefinitions();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var views = await db.SavedViews
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.Surface)
            .ThenByDescending(x => x.IsPinned)
            .ThenBy(x => x.Name)
            .Select(x => new
            {
                x.Id,
                x.Surface,
                x.Name,
                x.QueryString,
                x.IsPinned,
                x.CreatedAt,
                x.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        Rows = views
            .Select(x => new SavedViewRow(
                x.Id,
                x.Surface,
                policy.GetDefinition(x.Surface).DisplayName,
                x.Name,
                x.QueryString,
                policy.BuildUrl(x.Surface, x.QueryString),
                x.IsPinned,
                x.CreatedAt,
                x.UpdatedAt))
            .ToList();
    }

    public async Task<IActionResult> OnPostCreateAsync(
        SavedViewSurface surface,
        string name,
        string? queryString,
        bool isPinned,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId();

        try
        {
            var canonical = policy.Canonicalize(surface, queryString);
            var normalizedName = NormalizeNameKey(name);

            if (await db.SavedViews.CountAsync(x => x.UserId == userId, cancellationToken) >= MaxSavedViewsPerUser)
            {
                return StatusAndRedirect(
                    $"A maximum of {MaxSavedViewsPerUser} personal saved views is supported.",
                    true,
                    returnUrl);
            }

            if (await db.SavedViews.AnyAsync(
                    x => x.UserId == userId && x.Surface == surface && x.NameKey == normalizedName,
                    cancellationToken))
            {
                return StatusAndRedirect(
                    $"A {policy.GetDefinition(surface).DisplayName} view named '{name.Trim()}' already exists.",
                    true,
                    returnUrl);
            }

            if (isPinned && await PinnedCountAsync(userId, cancellationToken) >= MaxPinnedViewsPerUser)
            {
                return StatusAndRedirect(
                    $"Only {MaxPinnedViewsPerUser} views can be pinned. Unpin one first.",
                    true,
                    returnUrl);
            }

            var now = DateTimeOffset.UtcNow;
            var savedView = SavedView.Create(userId, surface, name, canonical, isPinned, now);
            db.SavedViews.Add(savedView);
            await db.SaveChangesAsync(cancellationToken);

            return StatusAndRedirect($"Saved view '{savedView.Name}'.", false, returnUrl);
        }
        catch (ArgumentException exception)
        {
            return StatusAndRedirect(exception.Message, true, returnUrl);
        }
    }

    public async Task<IActionResult> OnPostRenameAsync(
        Guid id,
        string name,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var savedView = await db.SavedViews
            .SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, cancellationToken);
        if (savedView is null)
        {
            return NotFound();
        }

        try
        {
            var normalizedName = NormalizeNameKey(name);
            if (await db.SavedViews.AnyAsync(
                    x => x.UserId == userId &&
                         x.Surface == savedView.Surface &&
                         x.Id != savedView.Id &&
                         x.NameKey == normalizedName,
                    cancellationToken))
            {
                return StatusAndRedirect(
                    $"A {policy.GetDefinition(savedView.Surface).DisplayName} view named '{name.Trim()}' already exists.",
                    true,
                    returnUrl ?? "/saved-views");
            }

            savedView.Rename(name, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
            return StatusAndRedirect($"Renamed saved view to '{savedView.Name}'.", false, returnUrl ?? "/saved-views");
        }
        catch (ArgumentException exception)
        {
            return StatusAndRedirect(exception.Message, true, returnUrl ?? "/saved-views");
        }
    }

    public async Task<IActionResult> OnPostTogglePinAsync(
        Guid id,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var savedView = await db.SavedViews
            .SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, cancellationToken);
        if (savedView is null)
        {
            return NotFound();
        }

        if (!savedView.IsPinned && await PinnedCountAsync(userId, cancellationToken) >= MaxPinnedViewsPerUser)
        {
            return StatusAndRedirect(
                $"Only {MaxPinnedViewsPerUser} views can be pinned. Unpin one first.",
                true,
                returnUrl ?? "/saved-views");
        }

        savedView.SetPinned(!savedView.IsPinned, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return StatusAndRedirect(
            savedView.IsPinned ? $"Pinned '{savedView.Name}'." : $"Unpinned '{savedView.Name}'.",
            false,
            returnUrl ?? "/saved-views");
    }

    public async Task<IActionResult> OnPostDeleteAsync(
        Guid id,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var savedView = await db.SavedViews
            .SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, cancellationToken);
        if (savedView is null)
        {
            return NotFound();
        }

        var name = savedView.Name;
        db.SavedViews.Remove(savedView);
        await db.SaveChangesAsync(cancellationToken);
        return StatusAndRedirect($"Deleted saved view '{name}'.", false, returnUrl ?? "/saved-views");
    }

    private async Task<int> PinnedCountAsync(string userId, CancellationToken cancellationToken) =>
        await db.SavedViews.CountAsync(x => x.UserId == userId && x.IsPinned, cancellationToken);

    private string GetUserId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Saved views require an authenticated user id.");

    private IActionResult StatusAndRedirect(string message, bool isError, string? returnUrl)
    {
        TempData[SavedViewTempData.StatusKey] = message;
        TempData[SavedViewTempData.IsErrorKey] = isError;

        return !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToPage();
    }

    private static string NormalizeNameKey(string name)
    {
        var normalized = name?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Saved view name is required.", nameof(name));
        }

        if (normalized.Length > 120)
        {
            throw new ArgumentException("Saved view name cannot exceed 120 characters.", nameof(name));
        }

        return normalized.ToUpperInvariant();
    }

    public sealed record SavedViewRow(
        Guid Id,
        SavedViewSurface Surface,
        string SurfaceName,
        string Name,
        string QueryString,
        string Url,
        bool IsPinned,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}

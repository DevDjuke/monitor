using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Monitor.Web.Services;

namespace Monitor.Web.TagHelpers;

[HtmlTargetElement("header")]
public sealed class SavedViewsHeaderTagHelper(
    SavedViewQueryPolicy policy,
    IViewComponentHelper viewComponentHelper) : TagHelper
{
    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = null!;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        if (!policy.TryResolveSurface(ViewContext.HttpContext.Request.Path, out var surface))
        {
            return;
        }

        if (viewComponentHelper is IViewContextAware contextAware)
        {
            contextAware.Contextualize(ViewContext);
        }

        var savedViews = await viewComponentHelper.InvokeAsync("SavedViews", new { surface });
        output.PostElement.AppendHtml(savedViews);
    }
}

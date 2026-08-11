using Monitor.Domain;

namespace Monitor.Web.Services;

public sealed class ComponentControlMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (ComponentWorkBlockedException exception) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "component_work_blocked",
                componentId = exception.ComponentId,
                controlState = exception.ControlState,
                enabled = exception.Enabled,
                message = "This component is not accepting new runs. Complete the matching Resume or Enable control command first."
            }, context.RequestAborted);
        }
    }
}

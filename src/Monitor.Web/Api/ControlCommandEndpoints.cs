using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure;
using Monitor.Infrastructure.Control;
using Monitor.Web.Auth;

namespace Monitor.Web.Api;

public static class ControlCommandEndpoints
{
    private const int MaxJsonLength = 64 * 1024;

    public static IEndpointRouteBuilder MapControlCommandApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/components/{componentId:guid}/commands");
        api.AddEndpointFilter(async (context, next) =>
        {
            var httpContext = context.HttpContext;
            var authenticator = httpContext.RequestServices.GetRequiredService<IngestionCredentialAuthenticator>();
            var identity = await authenticator.AuthenticateAsync(
                httpContext,
                allowOperator: true,
                cancellationToken: httpContext.RequestAborted,
                allowDisabledComponent: true);

            if (identity is null)
            {
                return Results.Unauthorized();
            }

            IngestionCredentialAuthenticator.SetIdentity(httpContext, identity);
            return await next(context);
        });

        api.MapPost("/claim", ClaimNext);
        api.MapPost("/{commandId:guid}/complete", Complete);
        return endpoints;
    }

    private static async Task<IResult> ClaimNext(
        Guid componentId,
        HttpContext httpContext,
        MonitorDbContext db,
        ComponentCommandService service,
        CancellationToken cancellationToken)
    {
        var identity = IngestionCredentialAuthenticator.GetIdentity(httpContext);
        if (!identity.CanAccess(componentId))
        {
            return Forbidden();
        }

        if (!await db.Components.AnyAsync(x => x.Id == componentId, cancellationToken))
        {
            return Results.NotFound();
        }

        var command = await service.ClaimNextAsync(componentId, cancellationToken);
        return command is null
            ? Results.NoContent()
            : Results.Ok(new
            {
                command.Id,
                command.ComponentId,
                command.Type,
                command.TargetRunId,
                command.PayloadJson,
                command.CreatedAt,
                command.ExpiresAt,
                command.LeaseToken,
                command.LeaseExpiresAt,
                command.DeliveryAttempt
            });
    }

    private static async Task<IResult> Complete(
        Guid componentId,
        Guid commandId,
        CompleteComponentCommandRequest request,
        HttpContext httpContext,
        ComponentCommandService service,
        CancellationToken cancellationToken)
    {
        var identity = IngestionCredentialAuthenticator.GetIdentity(httpContext);
        if (!identity.CanAccess(componentId))
        {
            return Forbidden();
        }

        if (request.LeaseToken == Guid.Empty)
        {
            return Results.BadRequest(new { error = "leaseToken is required" });
        }

        if (TooLarge(request.ResultJson) || TooLarge(request.Error))
        {
            return Results.BadRequest(new { error = $"resultJson and error are limited to {MaxJsonLength} characters" });
        }

        var result = await service.CompleteAsync(
            componentId,
            commandId,
            request.LeaseToken,
            request.Outcome,
            request.ResultJson,
            request.Error,
            cancellationToken);

        if (!result.Found)
        {
            return Results.NotFound();
        }

        if (result.LeaseConflict)
        {
            return Results.Conflict(new
            {
                error = "The command lease is stale or no longer active.",
                status = result.Status
            });
        }

        return Results.Ok(new
        {
            status = result.Status,
            alreadyTerminal = result.AlreadyTerminal
        });
    }

    private static bool TooLarge(string? value) => value?.Length > MaxJsonLength;

    private static IResult Forbidden() => Results.StatusCode(StatusCodes.Status403Forbidden);

    private sealed record CompleteComponentCommandRequest(
        Guid LeaseToken,
        ComponentCommandOutcome Outcome,
        string? ResultJson,
        string? Error);
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PGPM.Api;
using PGPM.Auth.Rights;
using PGPM.Data;

namespace PGPM.Audit;

/// <summary>
/// One entry per write request (PNMTD, docs/03 §3.4): around every action with <see cref="AuditAttribute"/>, it opens the
/// request's <see cref="AuditScope"/>, and afterwards sets the outcome and writes what the request's own saves didn't: a
/// failure (in a context of its own, as the request's may hold what failed), or an action that saved nothing through the
/// change tracker. Ordered first, so it sees the 400 of a model that didn't bind as well.
/// </summary>
public sealed class AuditFilter(IDbContextFactory<PgpmDbContext> dbs, ILogger<AuditFilter> logger) : IAsyncActionFilter, IOrderedFilter
{
    public int Order => int.MinValue;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor.EndpointMetadata.OfType<AuditAttribute>().FirstOrDefault() is not { } audit)
        {
            await next();
            return;
        }
        var http = context.HttpContext;
        var scope = AuditScope.Begin(audit.Events, AuditWho.From(http), RequestOf(context.ActionDescriptor as ControllerActionDescriptor, http),
            http.TraceIdentifier, RouteId(http));
        int status;
        string? code = null;
        try
        {
            var executed = await next();
            (status, code) = executed.Exception is { } e && !executed.ExceptionHandled ? StatusOf(e) : (StatusOf(executed.Result, http), CodeOf(executed.Result));
        }
        catch (Exception e)
        {
            (status, code) = StatusOf(e);
            await CompleteAsync(scope, status, code);
            throw;
        }
        await CompleteAsync(scope, status, code);
    }

    private Task CompleteAsync(AuditScope scope, int status, string? code) => CompleteAsync(dbs, logger, scope, status, code);

    /// <summary>Ends a scope and writes what its request's own saves didn't.</summary>
    internal static async Task CompleteAsync(IDbContextFactory<PgpmDbContext> dbs, ILogger logger, AuditScope scope, int status, string? code)
    {
        scope.End(status >= 400 ? AuditOutcome.Failed : AuditOutcome.Succeeded, status >= 400 ? $"{status}{(code is null ? "" : " " + code)}" : null);
        if (scope.Persisted && !scope.Dirty)
        {
            return;
        }
        try
        {
            await using var db = await dbs.CreateDbContextAsync(CancellationToken.None);
            var entry = scope.Apply();
            if (scope.Persisted)
            {
                db.AuditLog.Update(entry);
            }
            else
            {
                db.AuditLog.Add(entry);
            }
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogError(e, "The audit entry of {Request} could not be written", scope.Request);
        }
    }

    internal static Guid? RouteId(HttpContext http) =>
        http.Request.RouteValues.TryGetValue("id", out var id) && Guid.TryParse(id?.ToString(), out var guid) ? guid : null;

    /// <summary>"PUT /api/v1/users/{id:guid}", as the endpoint audit test names endpoints.</summary>
    public static string? RequestOf(ControllerActionDescriptor? action, HttpContext http) =>
        action?.AttributeRouteInfo?.Template is { } template ? $"{http.Request.Method} /{template}" : null;

    internal static (int Status, string? Code) StatusOf(Exception e) => e switch
    {
        ApiException a => (a.Status, a.Code),
        DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } => (409, ApiErrorCodes.DuplicateKey),
        OperationCanceledException => (499, "cancelled"),
        BadHttpRequestException b => (b.StatusCode, null),
        _ => (500, null),
    };

    private static int StatusOf(Microsoft.AspNetCore.Mvc.IActionResult? result, HttpContext http) => result switch
    {
        IStatusCodeActionResult { StatusCode: { } s } => s,
        null => http.Response.StatusCode,
        _ => 200,
    };

    /// <summary>The code of a ProblemDetails result (the exception filter's, for an ApiException the action threw).</summary>
    private static string? CodeOf(Microsoft.AspNetCore.Mvc.IActionResult? result) =>
        result is Microsoft.AspNetCore.Mvc.ObjectResult { Value: Microsoft.AspNetCore.Mvc.ProblemDetails problem }
        && problem.Extensions.TryGetValue("code", out var code) ? code?.ToString() : null;
}

/// <summary>
/// A signed-in user refused a write endpoint (403): a <c>denied</c> entry with the endpoint's event and the missing right.
/// Authorization runs before MVC's filters, so this decorates the middleware's result handler, which then says which
/// right is missing (<see cref="MissingRightResultHandler"/>). A burst is capped per user: 60 entries a minute, then one
/// "and further refusals" per minute.
/// </summary>
public sealed class AuditDeniedHandler(IDbContextFactory<PgpmDbContext> dbs, ILogger<AuditDeniedHandler> logger) : IAuthorizationMiddlewareResultHandler
{
    public const int PerMinute = 60;

    private readonly MissingRightResultHandler inner = new();
    private readonly Dictionary<string, (long Minute, int Count)> recent = [];
    private readonly Lock gate = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden && context.User.Identity?.IsAuthenticated == true
            && context.GetEndpoint()?.Metadata.GetMetadata<AuditAttribute>() is { } audit)
        {
            await DeniedAsync(context, audit, authorizeResult);
        }
        await inner.HandleAsync(next, context, policy, authorizeResult);
    }

    private async Task DeniedAsync(HttpContext context, AuditAttribute audit, PolicyAuthorizationResult result)
    {
        var who = AuditWho.From(context);
        var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        int count;
        lock (gate)
        {
            var key = $"{who.UserId}/{who.Username}";
            count = recent.TryGetValue(key, out var r) && r.Minute == minute ? r.Count + 1 : 1;
            recent[key] = (minute, count);
            if (recent.Count > 1000)
            {
                foreach (var stale in recent.Where(kv => kv.Value.Minute != minute).Select(kv => kv.Key).ToList())
                {
                    recent.Remove(stale);
                }
            }
        }
        if (count > PerMinute + 1)
        {
            return;
        }
        var action = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
        var missing = MissingRightResultHandler.Missing(result)?.ToString();
        var summary = count == PerMinute + 1 ? "and further refusals this minute, not listed one by one" : missing is null ? null : $"missing: {missing}";
        try
        {
            await using var db = await dbs.CreateDbContextAsync(context.RequestAborted);
            var subjectType = AuditCatalog.Get(audit.Events[0]).SubjectType;
            var target = AuditFilter.RouteId(context) is { } id && subjectType is not null ? new AuditTarget(subjectType, id, null) : null;
            db.AuditLog.Add(AuditLog.Entry(audit.Events[0], who, summary, target, AuditOutcome.Denied, context.TraceIdentifier,
                AuditFilter.RequestOf(action, context), "403"));
            await db.SaveChangesAsync(context.RequestAborted);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "The audit entry of a refused request could not be written");
        }
    }
}

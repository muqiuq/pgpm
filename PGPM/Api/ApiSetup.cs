using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace PGPM.Api;

public static class ApiPaths
{
    public const string V1 = "/api/v1";

    public static bool IsApi(PathString path) => path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// /api/v1's JSON and errors (docs/03 §3.3, from PNMTD):
/// <list type="bullet">
/// <item>JSON: web defaults (camelCase) with enums as strings and strict numbers, for controllers and minimal APIs alike;
///   the same options feed the OpenAPI document.</item>
/// <item>Errors: ProblemDetails with a stable <c>code</c>.</item>
/// </list>
/// </summary>
public static class ApiSetup
{
    public static IServiceCollection AddPgpmApi(this IServiceCollection services)
    {
        services.AddControllers().AddJsonOptions(o => ConfigureJson(o.JsonSerializerOptions));
        services.ConfigureHttpJsonOptions(o => ConfigureJson(o.SerializerOptions));

        // An IFormFile parameter would add a Consumes constraint, which routing checks before authorization: a caller
        // without the right would get 404 instead of 403.
        services.Configure<ApiBehaviorOptions>(o => o.SuppressConsumesConstraintForFormFileParameters = true);

        services.AddProblemDetails(o => o.CustomizeProblemDetails = context =>
        {
            var status = context.ProblemDetails.Status ?? context.HttpContext.Response.StatusCode;
            context.ProblemDetails.Extensions.TryAdd("code", ApiErrorCodes.ForStatus(status));
        });

        return services;
    }

    private static void ConfigureJson(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter());
        // Numbers are numbers: the web defaults also read "5" as 5, which types every number of the OpenAPI document as
        // number-or-string, and the UI's generated types with it.
        options.NumberHandling = JsonNumberHandling.Strict;
    }

    /// <summary>
    /// For /api only: unhandled exceptions and empty error responses (the cookie handler's 401 and 403, routing's 405)
    /// become ProblemDetails.
    /// </summary>
    public static IApplicationBuilder UseApiErrors(this IApplicationBuilder app) =>
        app.UseWhen(c => ApiPaths.IsApi(c.Request.Path), api =>
        {
            // Logged as well: the client only learns that something failed.
            api.UseExceptionHandler(new ExceptionHandlerOptions { SuppressDiagnosticsCallback = _ => false });
            api.UseStatusCodePages();
        });
}

/// <summary>Turns <see cref="ApiException"/> and unique-key violations into ProblemDetails.</summary>
public sealed class ApiExceptionFilterAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        var factory = context.HttpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>();
        ProblemDetails? problem = context.Exception switch
        {
            ApiException { Errors: not null } ex => WithCode(factory.CreateValidationProblemDetails(
                context.HttpContext, ToModelState(ex.Errors), ex.Status, detail: ex.Message), ex.Code),
            ApiException ex => WithCode(factory.CreateProblemDetails(context.HttpContext, ex.Status, detail: ex.Message), ex.Code),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } =>
                WithCode(factory.CreateProblemDetails(context.HttpContext, StatusCodes.Status409Conflict,
                    detail: "An entry with this key exists already."), ApiErrorCodes.DuplicateKey),
            _ => null,
        };
        if (problem is null)
        {
            return;
        }
        if (context.Exception is ApiException { Extensions: { } extensions })
        {
            foreach (var (name, value) in extensions)
            {
                problem.Extensions[name] = value;
            }
        }
        context.Result = new ObjectResult(problem)
        {
            StatusCode = problem.Status,
            ContentTypes = { "application/problem+json" },
        };
        context.ExceptionHandled = true;
    }

    private static ProblemDetails WithCode(ProblemDetails problem, string code)
    {
        problem.Extensions["code"] = code;
        return problem;
    }

    private static ModelStateDictionary ToModelState(IDictionary<string, string[]> errors)
    {
        var state = new ModelStateDictionary();
        foreach (var (field, messages) in errors)
        {
            foreach (var message in messages)
            {
                state.AddModelError(field, message);
            }
        }
        return state;
    }
}

/// <summary>The base of every v1 controller: JSON in and out, ProblemDetails for errors, CSRF for unsafe calls.</summary>
[ApiController]
[ApiExceptionFilter]
[Auth.CsrfProtection]
[Produces("application/json")]
public abstract class V1ControllerBase : ControllerBase;

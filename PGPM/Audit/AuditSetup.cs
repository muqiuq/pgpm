using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace PGPM.Audit;

public static class AuditSetup
{
    /// <summary>
    /// The audit log (docs/03 §3.4): the filter that gives every write request its entry, and the handler that records
    /// refusals. The change interceptor is the DbContext's (PgpmDbContext.Configure).
    /// </summary>
    public static IServiceCollection AddAudit(this IServiceCollection services)
    {
        services.AddScoped<AuditFilter>();
        services.Configure<MvcOptions>(o => o.Filters.AddService<AuditFilter>(int.MinValue));
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, AuditDeniedHandler>();
        return services;
    }
}

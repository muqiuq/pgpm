using PGPM.Auth;
using PGPM.Sharing;
using PGPM.Sync.Live;

namespace PGPM.Sync;

/// <summary>The sync core's services (docs/03 §11): the writer, the HTTP sync, sharing, trash, the live hub, the pruners.</summary>
public static class SyncSetup
{
    public static IServiceCollection AddSync(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SyncOptions>(configuration.GetSection("Sync"));
        services.AddScoped<SyncWriter>();
        services.AddScoped<SyncService>();
        services.AddScoped<SharingService>();
        services.AddScoped<TrashService>();
        services.AddSingleton<EditingRegistry>();
        // A singleton the controllers use, hosted (when the host runs hosted services) for the editing expiry and the shutdown.
        services.AddSingleton<LiveSyncHub>();
        return services;
    }

    /// <summary>The live hub as a hosted service: its shutdown says <c>app</c> to every client (not a background task).</summary>
    public static IServiceCollection AddLiveSyncHub(this IServiceCollection services) =>
        services.AddHostedService(sp => sp.GetRequiredService<LiveSyncHub>());

    /// <summary>The pruners (docs/03 §9), with the other background tasks.</summary>
    public static IServiceCollection AddSyncTasks(this IServiceCollection services)
    {
        services.AddHostedService<TombstonePrunerTask>();
        services.AddHostedService<LogPrunerTask>();
        return services;
    }

    /// <summary>After authentication: a signed-in request's changes of synced rows are the user's, from the UI (or MCP).</summary>
    public static IApplicationBuilder UseChangeContext(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.User.UserId() is not null)
            {
                using var _ = ChangeContext.Use(ChangeContext.Of(context, Mcp.McpPrincipal.IsMcp(context.User) ? ChangeSource.Mcp : ChangeSource.Ui));
                await next(context);
                return;
            }
            await next(context);
        });
}

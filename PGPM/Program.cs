using System.Reflection;
using PGPM.Api;
using PGPM.Audit;
using PGPM.Auth;
using PGPM.Data;
using PGPM.Hosting;
using PGPM.Sync;

namespace PGPM;

public partial class Program
{
    public static int Main(string[] args)
    {
        // `pgpm <command> …` (docker compose exec pgpm pgpm …): the command line, no web host (docs/03 §3.1).
        if (args.Length > 0 && Cli.CommandLine.IsCommand(args[0]))
        {
            return Cli.CommandLine.Run(args);
        }

        // The database is migrated by DatabaseMigrationService, the first hosted service.
        BuildApp(args).Run();
        return 0;
    }

    public static WebApplication BuildApp(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // The build writes the OpenAPI document by starting the host without a database (GetDocument.Insider).
        var documentBuild = Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";

        // Each feature registers itself (docs/03 §3.1). Later phases add AddFiles, AddPgpmMcp, …
        builder.Services.AddPgpmData(builder.Configuration);
        builder.Services.AddPgpmApi();
        builder.Services.AddPgpmOpenApi();
        builder.Services.AddPgpmAuth(builder.Configuration);
        builder.Services.AddAudit();
        builder.Services.AddPgpmHosting(builder.Configuration);
        builder.Services.AddSync(builder.Configuration);

        // First: every other hosted service starts on a migrated database. The live hub is no background task: it runs in
        // the tests too.
        if (!documentBuild)
        {
            builder.Services.AddHostedService<DatabaseMigrationService>();
            builder.Services.AddLiveSyncHub();
        }

        // Background tasks (docs/03 §9), without which the integration tests run (BackgroundTasks__Enabled=false).
        if (!documentBuild && builder.Configuration.GetValue("BackgroundTasks:Enabled", true))
        {
            builder.Services.AddSyncTasks();
        }

        // Console only: in a container that is `docker compose logs`, rotated by the log driver.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.AddDebug();

        var app = builder.Build();
        app.UsePgpmAuthSchemes();          // the Microsoft scheme only while Entra:Enabled

        // The order matters (docs/03 §3.1). First the client's real address and scheme (Proxy setting). There is no HTTPS
        // redirect: TLS ends at the reverse proxy.
        app.UseForwardedHeaders();
        app.UseWebSockets(HostingSetup.WebSocketOptions); // the live sync channel: /api/v1/sync/live
        app.UseSecurityHeaders();          // CSP and friends, for every response
        app.UseApiErrors();                // ProblemDetails, on /api only
        app.UseSpaFiles();                 // the web UI's files, anonymous (the shell holds no data)
        app.UseRouting();
        app.UsePlainNotFound();            // no endpoint: empty 404 (never the fallback policy's 401)
        app.UseRateLimiter();              // the login endpoints
        // Phase 6: app.UseMcpOriginCheck(). A browser's cookie never authenticates an MCP call.
        app.UseWhen(c => !HostingSetup.IsMcpPath(c.Request.Path), b => b.UseAuthentication());
        app.UseAuthorization();            // deny by default: the fallback policy wants a signed-in user
        app.UseSessionGates();             // a temporary password, a required 2FA setup, the day's code: first
        app.UseChangeContext();            // changes of synced rows are the signed-in user's (change_log, updated_by)

        // The container's health check: 200 while the database answers, 503 when it does not. Nothing else, so it may be
        // reachable from anywhere.
        app.MapGet("/healthz", async (PgpmDbContext db, CancellationToken ct) =>
                await db.Database.CanConnectAsync(ct)
                    ? Results.Text("ok")
                    : Results.Text("unavailable", statusCode: StatusCodes.Status503ServiceUnavailable))
            .AllowAnonymous()
            .ExcludeFromDescription();

        app.MapControllers();              // /api/v1/**
        // Phase 6: app.MapPgpmMcp();       // /mcp
        app.MapSpaFallback();              // deep links get index.html; unknown /api paths a ProblemDetails 404

        return app;
    }
}

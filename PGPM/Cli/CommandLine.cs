using PGPM.Data;

namespace PGPM.Cli;

/// <summary>
/// `pgpm &lt;command&gt;`: administration without the web host (docs/03 §3.1, from PNMTD). In the image,
/// /usr/local/bin/pgpm runs `dotnet /app/PGPM.dll "$@"`; the deploy scripts call it through
/// `docker compose exec pgpm pgpm …`. Exit codes: 0 done, 1 failed, 2 usage. Phase 1 adds `users` and `groups`,
/// phase 6 `directory`.
/// </summary>
public static class CommandLine
{
    private static readonly string[] Commands = ["db", "help", "--help", "-h"];

    public static bool IsCommand(string arg) => Commands.Contains(arg);

    private const string Usage = """
        Usage: pgpm <command>

          db migrate        apply pending database migrations (the server does it at every start, too)

        Without a command, pgpm runs the server. The database comes from the Database:* settings
        (Database__Host, Database__Password, … in the environment).
        """;

    public static int Run(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
        var logger = loggerFactory.CreateLogger("pgpm");
        try
        {
            return args switch
            {
                ["db", "migrate"] => Migrate(logger),
                _ => UsageError(args[0] is "help" or "--help" or "-h" ? null : $"Unknown command: {string.Join(' ', args)}"),
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "pgpm {Command} failed", args[0]);
            return 1;
        }
    }

    private static int UsageError(string? message)
    {
        if (message is not null)
        {
            Console.Error.WriteLine(message);
            Console.Error.WriteLine();
        }
        (message is null ? Console.Out : Console.Error).WriteLine(Usage);
        return message is null ? 0 : 2;
    }

    internal static IConfiguration Configuration()
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
        return new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
    }

    internal static PgpmDbContext Database() => PgpmDbContext.Create(DatabaseSettings.ConnectionString(Configuration()));

    private static int Migrate(ILogger logger)
    {
        using var db = Database();
        DatabaseMigrator.Migrate(db, logger);
        logger.LogInformation("The database is up to date.");
        return 0;
    }
}

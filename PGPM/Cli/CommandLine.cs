using PGPM.Data;

namespace PGPM.Cli;

/// <summary>
/// `pgpm &lt;command&gt;`: administration without the web host (docs/03 §3.1, from PNMTD). In the image,
/// /usr/local/bin/pgpm runs `dotnet /app/PGPM.dll "$@"`; the deploy scripts call it through
/// `docker compose exec pgpm pgpm …`. Exit codes: 0 done, 1 failed, 2 usage. Phase 6 adds `directory`.
/// </summary>
public static class CommandLine
{
    private static readonly string[] Commands = ["db", "users", "groups", "help", "--help", "-h"];

    public static bool IsCommand(string arg) => Commands.Contains(arg);

    private const string Usage = """
        Usage: pgpm <command>

          db migrate                          apply pending database migrations (the server does it at every start, too)
          users list                          every user: admin or groups, state, 2FA, sign-in, lockout, last sign-in
          users add <name> [--admin] [--groups <group,group>] [--display-name <text>] [--email <address>] [--password-stdin]
                                              a local user with a temporary password (shown once, 7 days), or the one
                                              on standard input; the rights come from the admin flag or the groups
          users reset-password <name> [--password-stdin]
                                              a temporary password (or the one on standard input); ends the sessions,
                                              lifts a lockout
          users reset-totp <name>             turns 2FA off (a lost authenticator); ends the sessions, lifts a lockout
          users unlock <name>                 lifts a lockout
          users set-admin <name> <on|off>     the admin flag; ends the user's sessions
          groups list                         every group: its members and rights

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
                ["users", .. var rest] => UsersCommand.Run(rest, Database, logger),
                ["groups", "list"] => UsersCommand.Groups(Database, logger),
                _ => UsageError(args[0] is "help" or "--help" or "-h" ? null : $"Unknown command: {string.Join(' ', args)}"),
            };
        }
        catch (CommandLineException ex)
        {
            return UsageError(ex.Message);
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

    /// <summary>`--name value` and `--flag` options of a command, each used at most once.</summary>
    internal sealed class Options
    {
        private readonly Dictionary<string, string?> values = new(StringComparer.Ordinal);
        private readonly HashSet<string> used = new(StringComparer.Ordinal);

        public Options(string[] args)
        {
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new CommandLineException($"Unexpected argument: {args[i]}");
                }
                var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
                values[args[i]] = hasValue ? args[++i] : null;
            }
        }

        public string? Optional(string name)
        {
            used.Add(name);
            if (!values.TryGetValue(name, out var value))
            {
                return null;
            }
            return value ?? throw new CommandLineException($"{name} needs a value.");
        }

        public bool Flag(string name)
        {
            used.Add(name);
            if (!values.TryGetValue(name, out var value))
            {
                return false;
            }
            return value is null ? true : throw new CommandLineException($"{name} takes no value.");
        }

        public void EnsureAllUsed()
        {
            var unknown = values.Keys.Where(k => !used.Contains(k)).ToList();
            if (unknown.Count > 0)
            {
                throw new CommandLineException($"Unknown option(s): {string.Join(", ", unknown)}");
            }
        }
    }
}

/// <summary>A command line that can't be run as it was typed: exit code 2 with the usage.</summary>
public sealed class CommandLineException(string message) : Exception(message);

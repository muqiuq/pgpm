using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PGPM.Data;
using PGPM.Lib.Helper;

namespace PGPM.Tests;

/// <summary>
/// Once per test run: a fresh PostgreSQL database (pgpm_test) on the server PGPM_TEST_DB names, by default the one
/// tools/test-db.sh starts, migrated before any app starts; a stand-in for the UI build; and the settings the tests need.
/// </summary>
[TestClass]
public static class TestDefaults
{
    public const string DefaultServer = "Host=127.0.0.1;Port=55432;Username=postgres;Password=test";

    public const string IndexHtml =
        "<!doctype html><html><head><script type=\"module\" src=\"/assets/index-3f9a.js\"></script></head><body><div id=\"app\"></div></body></html>";

    /// <summary>The UI build's stand-in in wwwroot: index.html, one hashed asset, the service worker and the manifest.</summary>
    public static string WebRoot { get; private set; } = null!;

    public static IConfiguration Configuration { get; private set; } = null!;

    [AssemblyInitialize]
    public static void Apply(TestContext _)
    {
        // Warnings and errors only: the Development settings log every SQL command.
        Environment.SetEnvironmentVariable("Logging__LogLevel__Default", "Warning");
        Environment.SetEnvironmentVariable("Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command", "Warning");
        // No background task under the tests' feet.
        Environment.SetEnvironmentVariable("BackgroundTasks__Enabled", "false");
        Environment.SetEnvironmentVariable("Database__ConnectionString", CreateDatabase("pgpm_test"));
        // Not UTC by default: time checks are vacuous in UTC (run-tests.sh runs them east and west of it).
        var zone = Environment.GetEnvironmentVariable("PGPM_TEST_TZ") ?? "Europe/Zurich";
        Clock.TimeZone = Clock.FromName(zone);

        var scratch = Directory.CreateTempSubdirectory("pgpm-tests-");
        Environment.SetEnvironmentVariable("DataProtection__KeysPath", Path.Combine(scratch.FullName, "keys"));
        WebRoot = CreateWebRoot(scratch.FullName);

        Configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        using var db = PgpmDbContext.Create(DatabaseSettings.ConnectionString(Configuration));
        DatabaseMigrator.Migrate(db, NullLogger.Instance);
    }

    private static string CreateWebRoot(string scratch)
    {
        var root = Path.Combine(scratch, "wwwroot");
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        File.WriteAllText(Path.Combine(root, "index.html"), IndexHtml);
        File.WriteAllText(Path.Combine(root, "assets", "index-3f9a.js"), "console.log('pgpm');");
        File.WriteAllText(Path.Combine(root, "sw.js"), "self.addEventListener('fetch', () => {});");
        File.WriteAllText(Path.Combine(root, "manifest.webmanifest"), "{\"name\":\"PGPM\"}");
        return root;
    }

    /// <summary>The run's server, with another database.</summary>
    public static string ConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("PGPM_TEST_DB") ?? DefaultServer) { Database = database }
            .ConnectionString;

    /// <summary>Dropped if it exists and created empty, without the migrations.</summary>
    public static string CreateDatabase(string name)
    {
        var server = new NpgsqlConnectionStringBuilder(ConnectionString("postgres")) { Pooling = false };
        try
        {
            using var connection = new NpgsqlConnection(server.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"DROP DATABASE IF EXISTS {name} WITH (FORCE); CREATE DATABASE {name};";
            command.ExecuteNonQuery();
        }
        catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException)
        {
            throw new InvalidOperationException(
                $"No PostgreSQL for the tests at {server.Host}:{server.Port}: run tools/test-db.sh, or set PGPM_TEST_DB. ({ex.Message})", ex);
        }
        return ConnectionString(name);
    }
}

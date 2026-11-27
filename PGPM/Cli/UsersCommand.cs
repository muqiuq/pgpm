using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PGPM.Api;
using PGPM.Audit;
using PGPM.Auth;
using PGPM.Auth.Rights;
using PGPM.Data;
using PGPM.Lib.Helper;

namespace PGPM.Cli;

/// <summary>
/// `pgpm users …` and `pgpm groups list` (PNMTD, docs/03 §4.6), behind deploy/add-user.sh, list-users.sh,
/// reset-password.sh and reset-totp.sh. Changes are logged with the actor system/cli. A running server notices ended
/// sessions within a minute (its session cache).
/// </summary>
internal static class UsersCommand
{
    public static int Run(string[] args, Func<PgpmDbContext> database, ILogger logger)
    {
        using var db = database();
        DatabaseMigrator.Migrate(db, logger);
        var accounts = new UserAccounts(db);
        try
        {
            return args switch
            {
                ["list"] => List(db),
                ["add", var name, .. var rest] => Add(accounts, name, new CommandLine.Options(rest)),
                ["reset-password", var name, .. var rest] => ResetPassword(accounts, name, new CommandLine.Options(rest)),
                ["reset-totp", var name] => ResetTotp(accounts, name),
                ["unlock", var name] => Unlock(accounts, name),
                ["set-admin", var name, var onOff] => SetAdmin(accounts, name, onOff),
                _ => throw new CommandLineException($"Unknown users command: {string.Join(' ', args)}"),
            };
        }
        catch (ApiException ex)
        {
            Console.Error.WriteLine(ex.Message);
            foreach (var (field, messages) in ex.Errors ?? new Dictionary<string, string[]>())
            {
                Console.Error.WriteLine($"  {field}: {string.Join(" ", messages)}");
            }
            return 1;
        }
    }

    private static int List(PgpmDbContext db)
    {
        var users = db.Users.AsNoTracking().Include(u => u.Groups).ThenInclude(m => m.Group).OrderBy(u => u.NormalizedUsername).ToList();
        var now = Clock.UtcNow;
        var rows = users.Select(u => new[]
        {
            u.Username,
            UserAccounts.Access(u.IsAdmin, u.Groups.Select(m => m.Group.Name).Order(StringComparer.OrdinalIgnoreCase)),
            u.IsActive ? "active" : "inactive",
            u.IsTotpEnabled ? "2FA" : "-",
            u.HasMicrosoftAccount ? u.PasswordHash is null ? "Microsoft" : "Microsoft+password" : "password",
            u.LockoutEnd is { } end && end > now ? $"locked until {end:yyyy-MM-dd HH:mm} UTC" : u.MustChangePassword ? "must change password" : "-",
            u.LastLoginAt is { } last ? $"{last:yyyy-MM-dd HH:mm} UTC" : "never",
            u.DisplayName,
        }).ToList();
        var header = new[] { "USERNAME", "ACCESS", "STATE", "2FA", "SIGN-IN", "NOTE", "LAST SIGN-IN", "NAME" };
        var widths = header.Select((h, i) => rows.Select(r => r[i].Length).Append(h.Length).Max()).ToArray();
        foreach (var row in rows.Prepend(header))
        {
            Console.WriteLine(string.Join("  ", row.Select((cell, i) => i == row.Length - 1 ? cell : cell.PadRight(widths[i]))).TrimEnd());
        }
        Console.WriteLine($"{users.Count} user(s).");
        return 0;
    }

    private static int Add(UserAccounts accounts, string name, CommandLine.Options options)
    {
        var admin = options.Flag("--admin");
        var groupNames = options.Optional("--groups");
        var displayName = options.Optional("--display-name");
        var email = options.Optional("--email");
        var fromStdin = options.Flag("--password-stdin");
        options.EnsureAllUsed();
        var groups = GroupIds(accounts.Db, groupNames);
        var (user, temporary) = accounts.CreateAsync(name, displayName, email, admin, groups.Select(g => g.Id).ToList(), fromStdin ? ReadPassword() : null,
            AuditWho.Cli).GetAwaiter().GetResult();
        Console.WriteLine($"Created {user.Username} ({UserAccounts.Access(admin, groups.Select(g => g.Name))}).");
        if (!admin && groups.Count == 0)
        {
            Console.WriteLine("No groups: they can sign in and see what is shared with them, nothing else, until an admin adds them to a group.");
        }
        PrintTemporary(user, temporary);
        return 0;
    }

    private static int ResetPassword(UserAccounts accounts, string name, CommandLine.Options options)
    {
        var fromStdin = options.Flag("--password-stdin");
        options.EnsureAllUsed();
        var user = Find(accounts, name);
        var temporary = accounts.ResetPasswordAsync(user.Id, fromStdin ? ReadPassword() : null, AuditWho.Cli).GetAwaiter().GetResult();
        Console.WriteLine($"Reset the password of {user.Username}: their sessions end, a lockout is lifted.");
        PrintTemporary(user, temporary);
        return 0;
    }

    private static int ResetTotp(UserAccounts accounts, string name)
    {
        var user = Find(accounts, name);
        var wasEnabled = accounts.ResetTotpAsync(user.Id, AuditWho.Cli).GetAwaiter().GetResult();
        Console.WriteLine(wasEnabled
            ? $"Turned 2FA off for {user.Username}: their sessions end, a lockout is lifted. The next sign-in offers the setup again."
            : $"{user.Username} had no 2FA. Their sessions end and a lockout is lifted all the same.");
        return 0;
    }

    private static int Unlock(UserAccounts accounts, string name)
    {
        var user = Find(accounts, name);
        accounts.UnlockAsync(user.Id, AuditWho.Cli).GetAwaiter().GetResult();
        Console.WriteLine($"Unlocked {user.Username}.");
        return 0;
    }

    private static int SetAdmin(UserAccounts accounts, string name, string onOff)
    {
        var admin = onOff switch
        {
            "on" => true,
            "off" => false,
            _ => throw new CommandLineException("users set-admin <name> on|off"),
        };
        var user = Find(accounts, name);
        accounts.UpdateAsync(user.Id, user.DisplayName, user.Email, admin, user.IsActive, null, AuditWho.Cli).GetAwaiter().GetResult();
        Console.WriteLine(admin ? $"{user.Username} is an admin now; their sessions end." : $"{user.Username} is no longer an admin; their sessions end.");
        return 0;
    }

    /// <summary>`pgpm groups list`: every group, its members and its rights.</summary>
    public static int Groups(Func<PgpmDbContext> database, ILogger logger)
    {
        using var db = database();
        DatabaseMigrator.Migrate(db, logger);
        var groups = db.Groups.AsNoTracking().Include(g => g.Rights).OrderBy(g => g.NormalizedName).ToList();
        foreach (var group in groups)
        {
            var members = db.UserGroups.AsNoTracking().Where(m => m.GroupId == group.Id).Join(db.Users, m => m.UserId, u => u.Id, (m, u) => u.Username)
                .OrderBy(n => n).ToList();
            var rights = RightsCatalog.Areas.Where(a => group.Rights.Any(r => r.Area == a.Area))
                .Select(a => $"{a.Label} {group.Rights.First(r => r.Area == a.Area).Level}");
            Console.WriteLine($"{group.Name}{(group.EntraAppRole is null ? "" : $" (Entra app role {group.EntraAppRole})")}: "
                + (members.Count == 0 ? "no members" : string.Join(", ", members)));
            Console.WriteLine($"    {string.Join(", ", rights.DefaultIfEmpty("no rights"))}");
        }
        Console.WriteLine($"{groups.Count} group(s). Admins have every right, whatever their groups.");
        return 0;
    }

    /// <summary>"Members,Viewers" to the groups; an unknown name is an error.</summary>
    private static List<GroupEntity> GroupIds(PgpmDbContext db, string? names)
    {
        if (string.IsNullOrWhiteSpace(names))
        {
            return [];
        }
        var wanted = names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(GroupEntity.Normalize).ToList();
        var groups = db.Groups.Where(g => wanted.Contains(g.NormalizedName)).ToList();
        var unknown = wanted.Where(w => groups.All(g => g.NormalizedName != w)).ToList();
        if (unknown.Count > 0)
        {
            throw new CommandLineException($"No such group: {string.Join(", ", unknown)} (pgpm groups list shows them).");
        }
        return groups;
    }

    private static UserEntity Find(UserAccounts accounts, string name) =>
        accounts.FindAsync(name).GetAwaiter().GetResult() ?? throw new ApiException(404, ApiErrorCodes.NotFound, $"No user {name}.");

    private static string ReadPassword() =>
        Console.In.ReadLine() is { Length: > 0 } line ? line : throw new CommandLineException("--password-stdin: no password on standard input.");

    private static void PrintTemporary(UserEntity user, string? temporary)
    {
        if (temporary is null)
        {
            return;
        }
        var until = user.TemporaryPasswordExpiresAt!.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        Console.WriteLine($"Temporary password, shown only now (valid until {until} UTC, to be changed at the first sign-in):");
        Console.WriteLine();
        Console.WriteLine($"    {temporary}");
        Console.WriteLine();
    }
}

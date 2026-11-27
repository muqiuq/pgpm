using System.Text.RegularExpressions;

namespace PGPM.Audit;

/// <summary>
/// The audit log's events (docs/02 §12): dotted, lower case, <c>&lt;subject&gt;.&lt;what happened&gt;</c>. Every event used
/// in code is in <see cref="AuditCatalog"/>, which gives it a label, a tone and the type of its subject.
/// </summary>
public static class AuditEvents
{
    // The sign-in (anonymous steps: the actor is the person signing in, or nobody for an unknown name).
    public const string LoginSucceeded = "auth.login_succeeded";
    public const string LoginFailed = "auth.login_failed";
    public const string LoginRefused = "auth.login_refused";
    public const string LockedOut = "auth.locked_out";
    public const string TotpFailed = "auth.totp_failed";
    public const string TotpDailyConfirmed = "auth.totp_daily_confirmed";
    public const string SsoFailed = "auth.sso_failed";
    public const string Logout = "auth.logout";

    // One's own account.
    public const string PasswordChanged = "account.password_changed";
    public const string SessionsRevoked = "account.sessions_revoked";
    public const string TotpSetupStarted = "account.totp_setup_started";
    public const string TotpEnabled = "account.totp_enabled";
    public const string TotpDisabled = "account.totp_disabled";

    // Users (admins, the CLI, the first start, Entra).
    public const string UserCreated = "user.created";
    public const string UserProvisioned = "user.provisioned";
    public const string UserUpdated = "user.updated";
    public const string UserAdminChanged = "user.admin_changed";
    public const string UserGroupsChanged = "user.groups_changed";
    public const string UserDeleted = "user.deleted";
    public const string PasswordReset = "user.password_reset";
    public const string TotpReset = "user.totp_reset";
    public const string UserUnlocked = "user.unlocked";
    public const string MicrosoftLinked = "user.microsoft_linked";
    public const string MicrosoftUnlinked = "user.microsoft_unlinked";

    // Groups and rights.
    public const string GroupCreated = "group.created";
    public const string GroupUpdated = "group.updated";
    public const string GroupRightsChanged = "group.rights_changed";
    public const string GroupMembersChanged = "group.members_changed";
    public const string GroupDeleted = "group.deleted";

    /// <summary>A membership set by Entra's app roles, or the default group (actor <c>entra</c>).</summary>
    public const string MembershipAdded = "membership.added";

    public const string MembershipRemoved = "membership.removed";

    // Persons (phase 1: only what a user's sign-in does to their person).
    public const string PersonCreated = "person.created";
    public const string PersonLinked = "person.linked";

    // Items: sharing and ownership (the field changes are in the change log).
    public const string ItemSharesChanged = "item.shares_changed";
    public const string ItemOwnerChanged = "item.owner_changed";

    // The Trash.
    public const string TrashRestored = "trash.restored";
    public const string TrashPurged = "trash.purged";

    // Maps: sharing and ownership.
    public const string MapSharesChanged = "map.shares_changed";
    public const string MapOwnerChanged = "map.owner_changed";

    // Configuration (statuses, sections, fields, templates, roles) and configuration packages.
    public const string ConfigCreated = "config.created";
    public const string ConfigUpdated = "config.updated";
    public const string ConfigArchived = "config.archived";
    public const string ConfigUnarchived = "config.unarchived";
    public const string ConfigReordered = "config.reordered";
    public const string ConfigDeleted = "config.deleted";
    public const string PackageImported = "config.package_imported";
    // MCP (docs/03 §7): the tokens, and what a token's tools change (actor mcp, actor_ref token:{id}:rw).
    public const string McpTokenOn = "mcp.token_on";
    public const string McpTokenOff = "mcp.token_off";
    public const string McpTokenRegenerated = "mcp.token_regenerated";
    public const string McpTokenExpired = "mcp.token_expired";
    public const string McpTokenBlocked = "mcp.token_blocked";
    public const string McpTokenOffByAdmin = "mcp.token_off_by_admin";
    public const string McpTokensRevoked = "mcp.tokens_revoked";
    public const string McpItemCreated = "mcp.item_created";
    public const string McpItemUpdated = "mcp.item_updated";
    public const string McpItemConverted = "mcp.item_converted";
    public const string McpMapChanged = "mcp.map_changed";

    // The directory sync (docs/03 §8).
    public const string DirectorySyncRequested = "directory.sync_requested";
    public const string DirectorySyncFailed = "directory.sync_failed";

    // Administration → System (docs/03 §9): a device made to start over.
    public const string SystemClientReset = "system.client_reset";
}

/// <summary>The types of subject an entry can be about (<c>audit_log.subject_type</c>).</summary>
public static class AuditSubjects
{
    public const string User = "user";
    public const string Group = "group";
    public const string Person = "person";
    public const string Item = "item";
    public const string Map = "map";
    public const string Config = "config";
    public const string McpToken = "mcp_token";
}

/// <summary>The colour of an event's tag.</summary>
public enum AuditTone { Neutral, Success, Warn, Danger }

/// <summary>An event of the catalog: a label for people, the tone of its tag, what it is about.</summary>
public sealed record AuditEventInfo(string Event, string Label, AuditTone Tone, string? SubjectType);

/// <summary>Every event with its label, tone and subject type, served by <c>GET /api/v1/audit/events</c> for the page's filter.</summary>
public static class AuditCatalog
{
    private static readonly List<AuditEventInfo> Entries = [];
    private static readonly Dictionary<string, AuditEventInfo> ByEvent = new(StringComparer.Ordinal);

    /// <summary>The events that always describe a failure.</summary>
    public static readonly IReadOnlySet<string> Failures = new HashSet<string>(StringComparer.Ordinal)
    {
        AuditEvents.LoginFailed, AuditEvents.LoginRefused, AuditEvents.TotpFailed, AuditEvents.LockedOut, AuditEvents.SsoFailed,
    };

    static AuditCatalog()
    {
        void Add(string? subject, params string[] events)
        {
            foreach (var e in events)
            {
                var info = new AuditEventInfo(e, Label(e), Tone(e), subject);
                Entries.Add(info);
                ByEvent.Add(e, info);
            }
        }

        Add(null, AuditEvents.LoginSucceeded, AuditEvents.LoginFailed, AuditEvents.LoginRefused, AuditEvents.LockedOut,
            AuditEvents.TotpFailed, AuditEvents.SsoFailed, AuditEvents.Logout);
        Add(AuditSubjects.User, AuditEvents.TotpDailyConfirmed, AuditEvents.PasswordChanged, AuditEvents.SessionsRevoked,
            AuditEvents.TotpSetupStarted, AuditEvents.TotpEnabled, AuditEvents.TotpDisabled);
        Add(AuditSubjects.User, AuditEvents.UserCreated, AuditEvents.UserProvisioned, AuditEvents.UserUpdated, AuditEvents.UserAdminChanged,
            AuditEvents.UserGroupsChanged, AuditEvents.UserDeleted, AuditEvents.PasswordReset, AuditEvents.TotpReset, AuditEvents.UserUnlocked,
            AuditEvents.MicrosoftLinked, AuditEvents.MicrosoftUnlinked);
        Add(AuditSubjects.Group, AuditEvents.GroupCreated, AuditEvents.GroupUpdated, AuditEvents.GroupRightsChanged,
            AuditEvents.GroupMembersChanged, AuditEvents.GroupDeleted, AuditEvents.MembershipAdded, AuditEvents.MembershipRemoved);
        Add(AuditSubjects.Person, AuditEvents.PersonCreated, AuditEvents.PersonLinked);
        Add(AuditSubjects.Item, AuditEvents.ItemSharesChanged, AuditEvents.ItemOwnerChanged, AuditEvents.TrashRestored, AuditEvents.TrashPurged);
        Add(AuditSubjects.Map, AuditEvents.MapSharesChanged, AuditEvents.MapOwnerChanged);
        Add(AuditSubjects.Config, AuditEvents.ConfigCreated, AuditEvents.ConfigUpdated, AuditEvents.ConfigArchived, AuditEvents.ConfigUnarchived,
            AuditEvents.ConfigReordered, AuditEvents.ConfigDeleted, AuditEvents.PackageImported);
        Add(AuditSubjects.McpToken, AuditEvents.McpTokenOn, AuditEvents.McpTokenOff, AuditEvents.McpTokenRegenerated, AuditEvents.McpTokenExpired,
            AuditEvents.McpTokenBlocked, AuditEvents.McpTokenOffByAdmin);
        Add(null, AuditEvents.McpTokensRevoked);
        Add(AuditSubjects.Item, AuditEvents.McpItemCreated, AuditEvents.McpItemUpdated, AuditEvents.McpItemConverted);
        Add(AuditSubjects.Map, AuditEvents.McpMapChanged);
        Add(null, AuditEvents.DirectorySyncRequested, AuditEvents.DirectorySyncFailed);
        Add(AuditSubjects.User, AuditEvents.SystemClientReset);
    }

    public static IReadOnlyList<AuditEventInfo> All => Entries;

    public static bool Contains(string e) => ByEvent.ContainsKey(e);

    /// <summary>The catalog's entry; for an event it doesn't know (an old row), one made up from the name.</summary>
    public static AuditEventInfo Get(string e) => ByEvent.TryGetValue(e, out var info) ? info : new AuditEventInfo(e, Label(e), Tone(e), null);

    private static readonly Dictionary<string, string> Words = new(StringComparer.Ordinal)
    {
        ["totp"] = "2FA", ["sso"] = "Microsoft sign-in", ["auth"] = "Sign-in", ["microsoft"] = "Microsoft",
    };

    /// <summary>"user.microsoft_linked" → "User: Microsoft linked".</summary>
    public static string Label(string e)
    {
        var dot = e.IndexOf('.');
        var subject = dot < 0 ? "" : Phrase(e[..dot]);
        var what = Phrase(dot < 0 ? e : e[(dot + 1)..]);
        return subject.Length == 0 ? what : $"{char.ToUpperInvariant(subject[0])}{subject[1..]}: {what}";

        static string Phrase(string part) => string.Join(' ', part.Split('_').Select(w => Words.TryGetValue(w, out var s) ? s : w));
    }

    private static AuditTone Tone(string e) =>
        Regex.IsMatch(e, "failed|locked_out|refused|blocked") ? AuditTone.Danger
        : Regex.IsMatch(e, "deleted|reset|revoked|disabled|unlinked|removed|expired|off_by_admin") ? AuditTone.Warn
        : Regex.IsMatch(e, "succeeded|enabled|created|provisioned|linked|added") ? AuditTone.Success
        : AuditTone.Neutral;
}

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PGPM.Data;
using PGPM.Lib.Helper;

namespace PGPM.Audit;

/// <summary>
/// Writing to the audit log (PNMTD's AuditLog, docs/03 §3.4), the one way for every part of PGPM: services in a write
/// request name the target and a summary of the request's entry; anything else (a side effect, the CLI, a seed, Entra)
/// records an entry of its own. Never a secret, in no field.
/// </summary>
public static class AuditLog
{
    public const int MaxSummary = 2000;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>In a write request: what it was on (its type, id and name then).</summary>
    public static void Target(AuditTarget target) => AuditScope.Current?.SetTarget(target);

    /// <summary>In a write request: the entry's summary, a sentence for people, instead of one made from the changes.</summary>
    public static void Summary(string summary) => AuditScope.Current?.SetSummary(summary);

    /// <summary>
    /// An event. In a write request whose endpoint names it, it becomes the request's entry; otherwise it is an entry of
    /// its own, added to <paramref name="db"/> and saved with the caller's <c>SaveChanges</c>, joined to the request's
    /// others by its correlation id.
    /// </summary>
    public static void Record(PgpmDbContext db, string @event, string? summary, AuditWho who, AuditTarget? target = null,
        AuditOutcome? outcome = null)
    {
        var scope = AuditScope.Current;
        if (scope is { Explicit: false } && scope.Events.Contains(@event) && outcome is null or AuditOutcome.Succeeded
            && who.Actor == scope.Who.Actor)
        {
            scope.Merge(@event, summary, target);
            return;
        }
        db.AuditLog.Add(Entry(@event, who, summary, target, outcome, scope?.CorrelationId, scope?.Request));
    }

    /// <summary>An entry, for <see cref="Record"/> and for writers that build their own.</summary>
    public static AuditLogEntity Entry(string @event, AuditWho who, string? summary, AuditTarget? target = null, AuditOutcome? outcome = null,
        string? correlationId = null, string? request = null, string? error = null, IReadOnlyList<AuditChange>? changes = null,
        int changesLeftOut = 0) => new()
    {
        Id = Ids.New(),
        At = Clock.UtcNow,
        Event = @event,
        Actor = who.Actor,
        ActorUserId = who.UserId,
        ActorRef = Cut(who.ActorRef, 200),
        SubjectType = target?.Type,
        SubjectId = target?.Id,
        Ip = who.IpAddress,
        CorrelationId = correlationId,
        Detail = JsonSerializer.Serialize(new AuditDetail
        {
            Username = who.Username.Length == 0 ? null : who.Username,
            SubjectName = Cut(target?.Name, 500),
            Summary = Cut(summary, MaxSummary),
            Outcome = outcome ?? (AuditCatalog.Failures.Contains(@event) ? AuditOutcome.Failed : AuditOutcome.Succeeded),
            Error = error,
            Request = request,
            UserAgent = string.IsNullOrEmpty(who.UserAgent) ? null : who.UserAgent,
            Changes = changes is { Count: > 0 } ? changes : null,
            ChangesLeftOut = changesLeftOut > 0 ? changesLeftOut : null,
        }, Json),
    };

    public static AuditDetail ReadDetail(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AuditDetail>(json, Json) ?? new AuditDetail();
        }
        catch (JsonException)
        {
            return new AuditDetail();
        }
    }

    /// <summary>The summary made from the changes: "jdoe: DisplayName Jo → Jo Doe, IsAdmin no → yes".</summary>
    public static string? Describe(string? name, IReadOnlyList<AuditChange> changes, int leftOut)
    {
        if (changes.Count == 0)
        {
            return name;
        }
        // An object created or deleted, with its own fields: "jdoe created" (the fields are in the changes).
        if (changes.FirstOrDefault(c => c.Kind is "created" or "deleted") is { } main && changes.All(c => c.Object == main.Object))
        {
            return Cut($"{name ?? main.Object} {main.Kind}", MaxSummary);
        }
        var parts = changes.Select(c => c.Kind switch
        {
            "secret" => $"{c.Field} changed",
            "text" => $"{c.Field} {c.Summary}",
            "created" => $"{c.Object ?? c.Field} created",
            "deleted" => $"{c.Object ?? c.Field} deleted",
            _ => $"{c.Field} {Show(c.Old)} → {Show(c.New)}",
        }).Distinct().ToList();
        var text = string.Join(", ", parts) + (leftOut > 0 ? $", and {leftOut} more" : "");
        return Cut(name is null ? text : $"{name}: {text}", MaxSummary);

        static string Show(string? value) => value is null or "" ? "–" : value.Length > 60 ? value[..59] + "…" : value;
    }

    public static string? Cut(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..(max - 1)] + "…";
}

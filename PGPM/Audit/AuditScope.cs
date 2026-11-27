using PGPM.Data;
using PGPM.Lib.Helper;

namespace PGPM.Audit;

/// <summary>
/// The audit of one write request (PNMTD): its event, who, the endpoint, and what the service and the change interceptor
/// add (the target, a summary, the changes). It lives in an <see cref="AsyncLocal{T}"/>, so every
/// <see cref="PgpmDbContext"/> of the request sees it, those from the factory too.
/// <para>
/// Its entry is added to the request's first <c>SaveChanges</c> (<see cref="AuditChangesInterceptor"/>), so a change is
/// never committed without it; later saves in the same context update it, and <see cref="AuditFilter"/> completes it at
/// the end: the outcome, and what other contexts or bulk writes added.
/// </para>
/// </summary>
public sealed class AuditScope
{
    public const int MaxChanges = 50;

    private static readonly AsyncLocal<AuditScope?> current = new();

    /// <summary>The running request's scope; none outside a write request, or once it ended.</summary>
    public static AuditScope? Current => current.Value is { Completed: false } scope ? scope : null;

    private AuditScope(IReadOnlyList<string> events, AuditWho who, string? request, string? correlationId, Guid? routeId)
    {
        Events = events;
        Event = events[0];
        Who = who;
        Request = request;
        CorrelationId = correlationId;
        RouteId = routeId;
        Entry = new AuditLogEntity { Id = Ids.New(), At = Clock.UtcNow, Event = Event, Detail = "{}" };
    }

    public static AuditScope Begin(IReadOnlyList<string> events, AuditWho who, string? request, string? correlationId, Guid? routeId)
    {
        var scope = new AuditScope(events, who, request, correlationId, routeId);
        current.Value = scope;
        return scope;
    }

    public IReadOnlyList<string> Events { get; }

    public string Event { get; private set; }

    public AuditWho Who { get; }

    public string? Request { get; }

    public string? CorrelationId { get; }

    /// <summary>The route's <c>{id}</c>: the first guess at the target.</summary>
    public Guid? RouteId { get; }

    public AuditTarget? Target { get; private set; }

    public string? Summary { get; private set; }

    public List<AuditChange> Changes { get; } = [];

    public int ChangesLeftOut { get; private set; }

    public AuditOutcome Outcome { get; private set; } = AuditOutcome.Succeeded;

    public string? Error { get; private set; }

    public bool Completed { get; private set; }

    /// <summary>A service recorded the endpoint's event itself (<see cref="AuditLog.Record"/>): its summary is the entry's.</summary>
    internal bool Explicit { get; private set; }

    internal AuditLogEntity Entry { get; }

    /// <summary>The context the entry was added to, while its save is under way or after.</summary>
    internal PgpmDbContext? Context { get; set; }

    internal bool Persisted { get; set; }

    /// <summary>Something changed after the entry was saved, which the filter writes at the end.</summary>
    internal bool Dirty { get; set; }

    public void SetTarget(AuditTarget target)
    {
        Target = target;
        Dirty = true;
    }

    /// <summary>The target the interceptor found, unless the service named one.</summary>
    internal void SuggestTarget(AuditTarget target)
    {
        if (Target is null || (Target.Type == target.Type && Target.Id == target.Id && Target.Name is null))
        {
            SetTarget(target);
        }
    }

    public void SetSummary(string? summary)
    {
        Summary = AuditLog.Cut(summary, AuditLog.MaxSummary);
        Dirty = true;
    }

    public void AddChange(AuditChange change)
    {
        if (Changes.Count < MaxChanges)
        {
            Changes.Add(change);
        }
        else
        {
            ChangesLeftOut++;
        }
        Dirty = true;
    }

    private (int Changes, int LeftOut, AuditTarget? Target)? checkpoint;

    /// <summary>Before a save: what to go back to if it fails, as its changes never happened.</summary>
    internal void Checkpoint() => checkpoint = (Changes.Count, ChangesLeftOut, Target);

    /// <summary>A save failed: the changes and the target it brought are dropped.</summary>
    internal void Restore()
    {
        if (checkpoint is not { } c)
        {
            return;
        }
        Changes.RemoveRange(c.Changes, Changes.Count - c.Changes);
        ChangesLeftOut = c.LeftOut;
        Target = c.Target;
        checkpoint = null;
    }

    /// <summary>A service's own record of one of the endpoint's events: it becomes the entry, with the service's summary.</summary>
    internal void Merge(string @event, string? summary, AuditTarget? target)
    {
        Event = @event;
        Explicit = true;
        SetSummary(summary);
        if (target is not null)
        {
            SetTarget(target);
        }
    }

    internal void End(AuditOutcome outcome, string? error)
    {
        if (outcome != Outcome || error != Error)
        {
            Dirty = true;
        }
        Outcome = outcome;
        Error = AuditLog.Cut(error, 200);
        Completed = true;
        if (current.Value == this)
        {
            current.Value = null;
        }
    }

    /// <summary>Writes the scope's state into its entry.</summary>
    internal AuditLogEntity Apply()
    {
        var info = AuditCatalog.Get(Event);
        var target = Target ?? (RouteId is { } id && info.SubjectType is not null ? new AuditTarget(info.SubjectType, id, null) : null);
        var made = AuditLog.Entry(Event, Who, Explicit ? Summary : Summary ?? AuditLog.Describe(target?.Name, Changes, ChangesLeftOut),
            target, Outcome, CorrelationId, Request, Error, Changes, ChangesLeftOut);
        var e = Entry;
        e.Event = made.Event;
        e.Actor = made.Actor;
        e.ActorUserId = made.ActorUserId;
        e.ActorRef = made.ActorRef;
        e.SubjectType = made.SubjectType;
        e.SubjectId = made.SubjectId;
        e.Ip = made.Ip;
        e.CorrelationId = made.CorrelationId;
        e.Detail = made.Detail;
        return e;
    }
}

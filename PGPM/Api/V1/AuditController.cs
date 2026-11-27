using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PGPM.Api.V1.Contracts;
using PGPM.Audit;
using PGPM.Auth.Rights;
using PGPM.Data;
using PGPM.Lib.Helper;

namespace PGPM.Api.V1;

/// <summary>The audit log (PNMTD, docs/02 §12): security and administration events, for <i>Audit log: View</i>.</summary>
[Route("api/v1/audit")]
[RequireRight(Area.AuditLog, Level.View)]
public sealed class AuditController(PgpmDbContext db) : V1ControllerBase
{
    /// <summary>Newest first; filters by event (or a family by its prefix), actor, outcome, a user (by or about), a subject, time and text.</summary>
    [HttpGet]
    public async Task<Paged<AuditEntryItem>> List([FromQuery] AuditQuery query, CancellationToken ct)
    {
        var log = db.AuditLog.AsNoTracking();
        if (query.Event is { Length: > 0 } e)
        {
            log = log.Where(l => l.Event == e);
        }
        if (query.Prefix is { Length: > 0 } prefix)
        {
            var start = prefix.EndsWith('.') ? prefix : prefix + ".";
            log = log.Where(l => l.Event.StartsWith(start));
        }
        if (query.Actor is { } actor)
        {
            log = log.Where(l => l.Actor == actor);
        }
        if (query.UserId is { } userId)
        {
            log = log.Where(l => l.ActorUserId == userId || l.SubjectId == userId);
        }
        if (query.SubjectId is { } subject)
        {
            log = log.Where(l => l.SubjectId == subject);
        }
        if (query.From is { } from)
        {
            var since = Clock.AsUtc(from);
            log = log.Where(l => l.At >= since);
        }
        if (query.To is { } to)
        {
            var until = Clock.AsUtc(to);
            log = log.Where(l => l.At < until);
        }
        // What lives in the jsonb detail: matched in SQL, as a subquery the filters above compose with.
        if (query.Outcome is { } outcome)
        {
            var value = outcome.ToString().ToLowerInvariant();
            var matching = db.AuditLog.FromSql($"SELECT * FROM audit_log WHERE detail->>'outcome' = {value}").Select(l => l.Id);
            log = log.Where(l => matching.Contains(l.Id));
        }
        if (SearchText.Pattern(query.Search) is { } pattern)
        {
            var matching = db.AuditLog.FromSql(
                $"SELECT * FROM audit_log WHERE detail::text ILIKE {pattern} OR event ILIKE {pattern} OR actor_ref ILIKE {pattern} OR ip ILIKE {pattern}")
                .Select(l => l.Id);
            log = log.Where(l => matching.Contains(l.Id));
        }
        var page = await Paging.PageAsync(log.OrderByDescending(l => l.At).ThenByDescending(l => l.Id), query, ct);
        return new Paged<AuditEntryItem>(page.Items.Select(ToItem).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    /// <summary>Every event with its label, tone and subject type: the filter's choices.</summary>
    [HttpGet("events")]
    public IReadOnlyList<AuditEventInfo> Events() => AuditCatalog.All;

    internal static AuditEntryItem ToItem(AuditLogEntity l)
    {
        var info = AuditCatalog.Get(l.Event);
        var d = AuditLog.ReadDetail(l.Detail);
        return new AuditEntryItem(l.Id, l.At, l.Event, info.Label, d.Outcome == AuditOutcome.Succeeded ? info.Tone : AuditTone.Danger, l.Actor,
            l.ActorUserId, d.Username, l.ActorRef, l.SubjectType, l.SubjectId, d.SubjectName, d.Summary, d.Outcome, d.Error, d.Request, l.Ip,
            d.UserAgent, d.Changes, l.CorrelationId);
    }
}

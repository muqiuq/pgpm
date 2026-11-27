using PGPM.Audit;

namespace PGPM.Api.V1.Contracts;

/// <summary>An entry of the audit log (docs/02 §12), with its detail unpacked.</summary>
public sealed record AuditEntryItem(
    Guid Id,
    DateTime At,
    string Event,
    string Label,
    AuditTone Tone,
    AuditActor Actor,
    Guid? ActorUserId,
    string? ActorName,
    string? ActorRef,
    string? SubjectType,
    Guid? SubjectId,
    string? SubjectName,
    string? Summary,
    AuditOutcome Outcome,
    string? Error,
    string? Request,
    string? Ip,
    string? UserAgent,
    IReadOnlyList<AuditChange>? Changes,
    string? CorrelationId);

/// <summary>
/// <c>GET /audit</c>: newest first. <see cref="UserId"/>: entries by or about the user; <see cref="Event"/>: one event;
/// <see cref="Prefix"/>: a family (<c>auth.</c>, <c>user.</c>, <c>group.</c>); from/to (UTC); search: names, summary.
/// </summary>
public sealed class AuditQuery : PageQuery
{
    public string? Event { get; set; }

    public string? Prefix { get; set; }

    public AuditActor? Actor { get; set; }

    public AuditOutcome? Outcome { get; set; }

    public Guid? UserId { get; set; }

    public Guid? SubjectId { get; set; }

    public DateTime? From { get; set; }

    public DateTime? To { get; set; }
}

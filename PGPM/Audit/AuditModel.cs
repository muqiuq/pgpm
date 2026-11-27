using PGPM.Auth;

namespace PGPM.Audit;

/// <summary>Who did it (docs/02 §12 <c>audit_log.actor</c>): a person, a person's MCP token, PGPM itself (the CLI, a seed, a job), or Entra.</summary>
public enum AuditActor { User, Mcp, System, Entra }

/// <summary>How it ended.</summary>
public enum AuditOutcome { Succeeded, Failed, Denied }

/// <summary>
/// Who did something, and from where: the person (or PGPM itself, or Entra), the address and the browser.
/// <see cref="ActorRef"/> says which when the actor isn't a person: <c>cli</c>, <c>seed</c>, an MCP token (phase 6).
/// </summary>
public sealed record AuditWho(AuditActor Actor, Guid? UserId, string Username, string? ActorRef, string? IpAddress, string? UserAgent)
{
    /// <summary>`pgpm users …` and the deploy scripts.</summary>
    public static readonly AuditWho Cli = new(AuditActor.System, null, "cli", "cli", null, null);

    /// <summary>The first start's seeds (the initial admin, the seeded groups).</summary>
    public static readonly AuditWho Seed = new(AuditActor.System, null, "system", "seed", null, null);

    public static readonly AuditWho System = new(AuditActor.System, null, "system", null, null, null);

    /// <summary>A Microsoft sign-in's consequences (provisioning, app-role memberships), for the user it signs in.</summary>
    public static AuditWho Entra(Guid? userId, string username, string? ipAddress) =>
        new(AuditActor.Entra, userId, username, "entra", ipAddress, null);

    /// <summary>
    /// The signed-in user of a request, or nobody (an anonymous step of the sign-in). An MCP token's request is the actor
    /// <c>mcp</c> in its user's name, with the token as <c>token:{id}:rw|ro</c> (docs/03 §7.2).
    /// </summary>
    public static AuditWho From(HttpContext http)
    {
        var agent = http.Request.Headers.UserAgent.ToString();
        var signedIn = http.User.Identity?.IsAuthenticated == true;
        var mcp = Mcp.McpPrincipal.Reference(http.User);
        return new AuditWho(mcp is null ? AuditActor.User : AuditActor.Mcp, signedIn ? http.User.UserId() : null,
            signedIn ? http.User.Identity!.Name ?? "" : "", mcp, IpOf(http), agent.Length > 256 ? agent[..256] : agent);
    }

    /// <summary>The directory job (docs/03 §8): PGPM itself.</summary>
    public static readonly AuditWho DirectoryJob = new(AuditActor.System, null, "system", "directory", null, null);

    /// <summary>The request's user, or the CLI when there is no request.</summary>
    public static AuditWho From(IHttpContextAccessor accessor) => accessor.HttpContext is { } http ? From(http) : Cli;

    /// <summary>The client's address as the forwarded headers left it; an IPv4-mapped IPv6 address as IPv4.</summary>
    public static string? IpOf(HttpContext http)
    {
        var address = http.Connection.RemoteIpAddress;
        return address is null ? null : (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }

    /// <summary>The same person, for an event in their name (a sign-in step: before it there is no signed-in user).</summary>
    public AuditWho As(Guid? userId, string username) => this with { UserId = userId, Username = username };
}

/// <summary>What an operation was on: a type of <see cref="AuditSubjects"/>, the object's id and its name as it was then.</summary>
public sealed record AuditTarget(string Type, Guid? Id, string? Name)
{
    public static AuditTarget User(Guid id, string username) => new(AuditSubjects.User, id, username);

    public static AuditTarget Group(Guid id, string name) => new(AuditSubjects.Group, id, name);

    public static AuditTarget Person(Guid id, string name) => new(AuditSubjects.Person, id, name);

    public static AuditTarget Item(Guid id, string title) => new(AuditSubjects.Item, id, title);

    public static AuditTarget Map(Guid id, string name) => new(AuditSubjects.Map, id, name);
}

/// <summary>One change of a field. A secret has no values; a long text a summary of its lines.</summary>
public sealed record AuditChange(string? Object, string Field, string? Old, string? New, string? Kind = null, string? Summary = null);

/// <summary>
/// <c>audit_log.detail</c> (jsonb): everything an entry says beyond its columns – the name of who did it and of the
/// subject as they were then, a sentence for people, the outcome, the endpoint, the browser and the field changes.
/// </summary>
public sealed record AuditDetail
{
    public string? Username { get; init; }

    public string? SubjectName { get; init; }

    public string? Summary { get; init; }

    public AuditOutcome Outcome { get; init; } = AuditOutcome.Succeeded;

    /// <summary>"403", "409 last_admin".</summary>
    public string? Error { get; init; }

    /// <summary>"PUT /api/v1/users/{id:guid}": the endpoint, as the endpoint audit test names it.</summary>
    public string? Request { get; init; }

    public string? UserAgent { get; init; }

    public IReadOnlyList<AuditChange>? Changes { get; init; }

    public int? ChangesLeftOut { get; init; }
}

/// <summary>
/// The event a write action makes: one entry per request, written by <see cref="AuditFilter"/>. The first event is the
/// entry's; a service recording one of the others (<see cref="AuditLog.Record"/>) makes the entry that event instead,
/// as <c>PUT /users/{id}</c> is a <c>user.updated</c> or a <c>user.admin_changed</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AuditAttribute(params string[] events) : Attribute
{
    public IReadOnlyList<string> Events { get; } = events.Length > 0 ? events : throw new ArgumentException("An event is needed.");
}

/// <summary>On a write action: it changes nothing a person did (or a service logs it itself), with the reason.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class NotAuditedAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}

/// <summary>An entity whose changes the audit records, field by field (docs/03 §3.4).</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AuditedAttribute : Attribute;

/// <summary>A secret: the audit records that it changed, never a value.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AuditSecretAttribute : Attribute;

/// <summary>Noise: timestamps of its own, counters, computed columns. Not recorded.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AuditIgnoreAttribute : Attribute;

/// <summary>A long text (Markdown, notes): the lines added and removed, not the text.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AuditTextAttribute : Attribute;

/// <summary>A row of the audit log (docs/02 §12 <c>audit_log</c>): security and administration events.</summary>
public sealed class AuditLogEntity
{
    public Guid Id { get; set; }

    public DateTime At { get; set; }

    public required string Event { get; set; }

    public AuditActor Actor { get; set; }

    public Guid? ActorUserId { get; set; }

    public string? ActorRef { get; set; }

    public string? SubjectType { get; set; }

    public Guid? SubjectId { get; set; }

    /// <summary><see cref="AuditDetail"/> as JSON.</summary>
    public required string Detail { get; set; }

    public string? Ip { get; set; }

    public string? CorrelationId { get; set; }
}

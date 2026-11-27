using PGPM.Auth;

namespace PGPM.Sync;

/// <summary>Where a change of a synced row comes from (docs/02 §12 <c>change_log.source</c>).</summary>
public enum ChangeSource { Ui, Mcp, Admin, Directory, Restore, System }

/// <summary>
/// Who changes synced rows right now, for <see cref="SyncStampInterceptor"/> (<c>created_by</c>, <c>updated_by</c>) and
/// <see cref="ChangeLogInterceptor"/> (<c>change_log</c>). It lives in an <see cref="AsyncLocal{T}"/>: the request's signed-in
/// user (<see cref="SyncSetup.UseChangeContext"/>), overridden by the sync push per mutation (its client time) and by
/// restore; nothing outside a request (the CLI, seeds, background tasks) is the system.
/// </summary>
public sealed record ChangeContext(Guid? UserId, ChangeSource Source, string? CorrelationId, DateTime? ClientChangedAt = null)
{
    public static readonly ChangeContext System = new(null, ChangeSource.System, null);

    private static readonly AsyncLocal<ChangeContext?> current = new();

    public static ChangeContext Current => current.Value ?? System;

    /// <summary>This context until the returned scope is disposed (in this async flow and the calls it makes).</summary>
    public static IDisposable Use(ChangeContext context)
    {
        var before = current.Value;
        current.Value = context;
        return new Restore(before);
    }

    /// <summary>The request's user, as the source given.</summary>
    public static ChangeContext Of(HttpContext http, ChangeSource source = ChangeSource.Ui) =>
        new(http.User.UserId(), source, http.TraceIdentifier);

    private sealed class Restore(ChangeContext? before) : IDisposable
    {
        public void Dispose() => current.Value = before;
    }
}

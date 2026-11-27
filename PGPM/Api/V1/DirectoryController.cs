using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PGPM.Api.V1.Contracts;
using PGPM.Audit;
using PGPM.Auth;
using PGPM.Auth.Microsoft;
using PGPM.Auth.Rights;
using PGPM.Data;
using PGPM.Persons;
using PGPM.Persons.DirectorySync;

namespace PGPM.Api.V1;

/// <summary>
/// Administration → Microsoft integration (docs/03 §8, docs/01 §16): the SSO's settings (read-only, from the
/// configuration), the directory sync's last run and counts, and <i>Run now</i>. <i>Directory: Manage</i>.
/// </summary>
[Route("api/v1/directory")]
[RequireRight(Area.Directory, Level.Manage)]
public sealed class DirectoryController(PgpmDbContext db, DirectorySyncService sync, DirectorySyncTask task, IOptionsMonitor<EntraOptions> entra,
    IConfiguration configuration) : V1ControllerBase
{
    [HttpGet("status")]
    public async Task<DirectoryStatusItem> Status(CancellationToken ct) => await StatusAsync(ct);

    /// <summary>
    /// <i>Run now</i> (<c>?full=true</c>: a full run, which makes persons the directory no longer has inactive): the
    /// background task wakes and runs. 202 with the status; 409 <c>directory_off</c> while <c>Directory:Enabled</c> is off
    /// or the app registration is incomplete.
    /// </summary>
    [HttpPost("sync")]
    [Audit(AuditEvents.DirectorySyncRequested)]
    public async Task<IActionResult> Run([FromQuery] bool full, CancellationToken ct)
    {
        if (!sync.Options.Enabled)
        {
            throw ApiException.Conflict("directory_off", "The directory sync is off (Directory:Enabled).");
        }
        if (sync.Credentials() is null)
        {
            throw ApiException.Conflict("directory_off", "The directory sync needs Entra:TenantId, Entra:ClientId and Entra:ClientSecret.");
        }
        task.RequestRun(full);
        AuditLog.Summary(full ? "A full run" : "A run");
        return Accepted(await StatusAsync(ct));
    }

    private async Task<DirectoryStatusItem> StatusAsync(CancellationToken ct)
    {
        var e = entra.CurrentValue;
        var o = sync.Options;
        var defaultGroup = e.DefaultGroupId is { } g
            ? await db.Groups.AsNoTracking().Where(x => x.Id == g).Select(x => new Ref(x.Id, x.Name)).FirstOrDefaultAsync(ct)
            : null;
        var roles = await db.Groups.AsNoTracking().Where(x => x.EntraAppRole != null).OrderBy(x => x.EntraAppRole).Select(x => x.EntraAppRole!).ToListAsync(ct);
        var publicUrl = MicrosoftSignIn.PublicUrl(configuration);
        var sso = new SsoSettingsItem(e.Enabled, e.TenantId, e.ClientId, !string.IsNullOrEmpty(e.ClientSecret),
            publicUrl is null ? null : new Uri(publicUrl, MicrosoftSignIn.CallbackPath).ToString(), e.AutoProvision, defaultGroup, roles);
        var credentials = sync.Credentials();
        var state = credentials is null ? null
            : await db.DirectorySyncState.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == credentials.TenantId, ct);
        var interval = Math.Max(1, o.IntervalHours);
        var directory = new DirectorySettingsItem(o.Enabled, credentials is not null, o.IntervalHours, o.FullSyncDays, task.Running, task.Pending,
            o.Enabled && credentials is not null ? state?.LastStartedAt?.AddHours(interval) ?? Lib.Helper.Clock.UtcNow : null,
            o.Enabled && credentials is not null ? state?.LastFullAt?.AddDays(Math.Max(1, o.FullSyncDays)) ?? Lib.Helper.Clock.UtcNow : null);
        var run = state is null ? null
            : new DirectoryRunItem(state.LastStartedAt, state.LastFinishedAt, state.LastStatus, state.LastMode, state.LastError, state.CreatedCount,
                state.UpdatedCount, state.DeactivatedCount, state.SeenCount, state.LinkedCount, state.LastFullAt, state.LastSucceededAt);
        var persons = db.Persons.AsNoTracking().Where(p => !p.Deleted);
        var microsoft = persons.Where(p => p.Source == PersonSource.Microsoft);
        var counts = new DirectoryCountsItem(await microsoft.CountAsync(ct), await microsoft.CountAsync(p => p.IsActive, ct),
            await microsoft.CountAsync(p => !p.IsActive, ct), await microsoft.CountAsync(p => p.UserType == "Guest", ct),
            await microsoft.CountAsync(p => p.UserId != null, ct), await persons.CountAsync(p => p.Source == PersonSource.Manual, ct));
        return new DirectoryStatusItem(sso, directory, run, counts);
    }
}

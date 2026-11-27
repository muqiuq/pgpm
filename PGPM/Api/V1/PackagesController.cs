using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using PGPM.Api.V1.Contracts;
using PGPM.Audit;
using PGPM.Auth.Rights;
using PGPM.Packages;
using PGPM.Sharing;
using PGPM.Sync;

namespace PGPM.Api.V1;

/// <summary>
/// Configuration packages (docs/03 §17): the built-in ones and what was imported, a dry run's diff, the import in one
/// transaction (every device gets it live), the export. <i>Configuration: Manage</i>; a package that changes groups and
/// their rights needs an admin (403 <c>admin_required</c>). An invalid package is 400 <c>invalid_package</c> with
/// <c>problems</c>.
/// </summary>
[Route("api/v1/config/packages")]
[RequireRight(Area.Configuration, Level.Manage)]
public sealed class PackagesController(PackageImporter packages) : V1ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<PackageInfo>> List(CancellationToken ct) => await packages.ListAsync(ct);

    /// <summary>What an import would do, entity by entity; changes nothing.</summary>
    [HttpPost("dry-run")]
    [NotAudited("Reads only.")]
    public async Task<PackageDiff> DryRun(PackageRequest request, CancellationToken ct) =>
        await packages.DryRunAsync(await Viewer.OfAsync(HttpContext, ct), PackageImporter.PackageOf(request), request.Overwrite, ct);

    /// <summary>The import: creates and updates, local edits kept unless <c>overwrite</c> names their keys.</summary>
    [HttpPost("apply")]
    [Audit(AuditEvents.PackageImported)]
    public async Task<PackageApplyResult> Apply(PackageRequest request, CancellationToken ct) =>
        await packages.ApplyAsync(await Viewer.OfAsync(HttpContext, ct), PackageImporter.PackageOf(request), request.Overwrite, ChangeSource.Admin, ct);

    /// <summary>The configuration as a package (all of it, or what <c>package</c> brought).</summary>
    [HttpGet("export")]
    public async Task<JsonObject> Export([FromQuery] string? package, CancellationToken ct) => await packages.ExportAsync(package, ct);
}

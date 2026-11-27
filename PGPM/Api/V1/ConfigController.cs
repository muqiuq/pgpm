using Microsoft.AspNetCore.Mvc;
using PGPM.Api.V1.Contracts;
using PGPM.Audit;
using PGPM.Auth.Rights;
using PGPM.Config;
using PGPM.Sharing;

namespace PGPM.Api.V1;

/// <summary>
/// The configuration's statuses (docs/01 §5, docs/02 §7.1): <i>Configuration: Manage</i>. Reads come from the sync; every
/// write reaches all devices live. Archived instead of deleted.
/// </summary>
[Route("api/v1/config/statuses")]
[RequireRight(Area.Configuration, Level.Manage)]
public sealed class ConfigStatusesController(ConfigService config) : V1ControllerBase
{
    [HttpPost]
    [Audit(AuditEvents.ConfigCreated)]
    public async Task<StatusItem> Create(StatusRequest request, CancellationToken ct) =>
        await config.CreateStatusAsync(await Viewer.OfAsync(HttpContext, ct), request, ct);

    [HttpPut("{id:guid}")]
    [Audit(AuditEvents.ConfigUpdated)]
    public async Task<StatusItem> Update(Guid id, StatusRequest request, CancellationToken ct) =>
        await config.UpdateStatusAsync(await Viewer.OfAsync(HttpContext, ct), id, request, ct);

    /// <summary>Not selectable any more, still shown; never the default (409 <c>default_status</c>).</summary>
    [HttpPost("{id:guid}/archive")]
    [Audit(AuditEvents.ConfigArchived)]
    public async Task<StatusItem> Archive(Guid id, CancellationToken ct) => await config.ArchiveStatusAsync(await Viewer.OfAsync(HttpContext, ct), id, true, ct);

    [HttpPost("{id:guid}/unarchive")]
    [Audit(AuditEvents.ConfigUnarchived)]
    public async Task<StatusItem> Unarchive(Guid id, CancellationToken ct) =>
        await config.ArchiveStatusAsync(await Viewer.OfAsync(HttpContext, ct), id, false, ct);

    /// <summary>All statuses of <c>itemType</c> in their new order.</summary>
    [HttpPut("order")]
    [Audit(AuditEvents.ConfigReordered)]
    public async Task<IReadOnlyList<StatusItem>> Order(OrderRequest request, CancellationToken ct) =>
        await config.OrderStatusesAsync(await Viewer.OfAsync(HttpContext, ct), request, ct);
}

/// <summary>
/// Sections and who may view and edit them (docs/01 §6.1, docs/02 §7.2–7.3). A change of the groups that may view a section
/// resets the affected users' devices.
/// </summary>
[Route("api/v1/config/sections")]
[RequireRight(Area.Configuration, Level.Manage)]
public sealed class ConfigSectionsController(ConfigService config) : V1ControllerBase
{
    [HttpPost]
    [Audit(AuditEvents.ConfigCreated)]
    public async Task<SectionItem> Create(SectionRequest request, CancellationToken ct) =>
        await config.CreateSectionAsync(await Viewer.OfAsync(HttpContext, ct), request, ct);

    [HttpPut("{id:guid}")]
    [Audit(AuditEvents.ConfigUpdated)]
    public async Task<SectionItem> Update(Guid id, SectionRequest request, CancellationToken ct) =>
        await config.UpdateSectionAsync(await Viewer.OfAsync(HttpContext, ct), id, request, ct);

    [HttpPost("{id:guid}/archive")]
    [Audit(AuditEvents.ConfigArchived)]
    public async Task<SectionItem> Archive(Guid id, CancellationToken ct) => await config.ArchiveSectionAsync(await Viewer.OfAsync(HttpContext, ct), id, true, ct);

    [HttpPost("{id:guid}/unarchive")]
    [Audit(AuditEvents.ConfigUnarchived)]
    public async Task<SectionItem> Unarchive(Guid id, CancellationToken ct) =>
        await config.ArchiveSectionAsync(await Viewer.OfAsync(HttpContext, ct), id, false, ct);

    [HttpPut("order")]
    [Audit(AuditEvents.ConfigReordered)]
    public async Task<IReadOnlyList<SectionItem>> Order(OrderRequest request, CancellationToken ct) =>
        await config.OrderSectionsAsync(await Viewer.OfAsync(HttpContext, ct), request, ct);
}

/// <summary>Fields of the sections (docs/01 §6.1, docs/02 §7.4): key and type immutable; scores validated (docs/03 §16).</summary>
[Route("api/v1/config/fields")]
[RequireRight(Area.Configuration, Level.Manage)]
public sealed class ConfigFieldsController(ConfigService config) : V1ControllerBase
{
    [HttpPost]
    [Audit(AuditEvents.ConfigCreated)]
    public async Task<FieldItem> Create(FieldRequest request, CancellationToken ct) =>
        await config.CreateFieldAsync(await Viewer.OfAsync(HttpContext, ct), request, ct);

    [HttpPut("{id:guid}")]
    [Audit(AuditEvents.ConfigUpdated)]
    public async Task<FieldItem> Update(Guid id, FieldRequest request, CancellationToken ct) =>
        await config.UpdateFieldAsync(await Viewer.OfAsync(HttpContext, ct), id, request, ct);

    /// <summary>Hidden from forms, values kept; not while a score uses it (409 <c>used_by_score</c>).</summary>
    [HttpPost("{id:guid}/archive")]
    [Audit(AuditEvents.ConfigArchived)]
    public async Task<FieldItem> Archive(Guid id, CancellationToken ct) => await config.ArchiveFieldAsync(await Viewer.OfAsync(HttpContext, ct), id, true, ct);

    [HttpPost("{id:guid}/unarchive")]
    [Audit(AuditEvents.ConfigUnarchived)]
    public async Task<FieldItem> Unarchive(Guid id, CancellationToken ct) =>
        await config.ArchiveFieldAsync(await Viewer.OfAsync(HttpContext, ct), id, false, ct);

    /// <summary>What deleting the field would remove: the number to confirm the delete with.</summary>
    [HttpGet("{id:guid}/usage")]
    public async Task<FieldUsage> Usage(Guid id, CancellationToken ct) => await config.FieldUsageAsync(id, ct);

    /// <summary>The field with its values (<c>?confirm=</c> their number, 409 <c>confirmation_required</c> otherwise).</summary>
    [HttpDelete("{id:guid}")]
    [Audit(AuditEvents.ConfigDeleted)]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] int? confirm, CancellationToken ct)
    {
        await config.DeleteFieldAsync(await Viewer.OfAsync(HttpContext, ct), id, confirm, ct);
        return NoContent();
    }

    /// <summary>All fields of <c>sectionId</c> in their new order.</summary>
    [HttpPut("order")]
    [Audit(AuditEvents.ConfigReordered)]
    public async Task<IReadOnlyList<FieldItem>> Order(OrderRequest request, CancellationToken ct) =>
        await config.OrderFieldsAsync(await Viewer.OfAsync(HttpContext, ct), request, ct);
}

/// <summary>Project templates with their statuses, sections, field defaults, milestones and default shares (docs/01 §10.2).</summary>
[Route("api/v1/config/templates")]
[RequireRight(Area.Configuration, Level.Manage)]
public sealed class ConfigTemplatesController(ConfigService config) : V1ControllerBase
{
    [HttpPost]
    [Audit(AuditEvents.ConfigCreated)]
    public async Task<TemplateDetail> Create(TemplateRequest request, CancellationToken ct) =>
        await config.CreateTemplateAsync(await Viewer.OfAsync(HttpContext, ct), request, ct);

    [HttpPut("{id:guid}")]
    [Audit(AuditEvents.ConfigUpdated)]
    public async Task<TemplateDetail> Update(Guid id, TemplateRequest request, CancellationToken ct) =>
        await config.UpdateTemplateAsync(await Viewer.OfAsync(HttpContext, ct), id, request, ct);

    [HttpPost("{id:guid}/archive")]
    [Audit(AuditEvents.ConfigArchived)]
    public async Task<TemplateDetail> Archive(Guid id, CancellationToken ct) =>
        await config.ArchiveTemplateAsync(await Viewer.OfAsync(HttpContext, ct), id, true, ct);

    [HttpPost("{id:guid}/unarchive")]
    [Audit(AuditEvents.ConfigUnarchived)]
    public async Task<TemplateDetail> Unarchive(Guid id, CancellationToken ct) =>
        await config.ArchiveTemplateAsync(await Viewer.OfAsync(HttpContext, ct), id, false, ct);

    [HttpPut("order")]
    [Audit(AuditEvents.ConfigReordered)]
    public async Task<IReadOnlyList<TemplateDetail>> Order(OrderRequest request, CancellationToken ct) =>
        await config.OrderTemplatesAsync(await Viewer.OfAsync(HttpContext, ct), request, ct);
}

/// <summary>Assignment roles, the suggestions for an assignment's role (docs/03 §17).</summary>
[Route("api/v1/config/roles")]
[RequireRight(Area.Configuration, Level.Manage)]
public sealed class ConfigRolesController(ConfigService config) : V1ControllerBase
{
    [HttpPost]
    [Audit(AuditEvents.ConfigCreated)]
    public async Task<RoleItem> Create(RoleRequest request, CancellationToken ct) => await config.CreateRoleAsync(await Viewer.OfAsync(HttpContext, ct), request, ct);

    [HttpPut("{id:guid}")]
    [Audit(AuditEvents.ConfigUpdated)]
    public async Task<RoleItem> Update(Guid id, RoleRequest request, CancellationToken ct) =>
        await config.UpdateRoleAsync(await Viewer.OfAsync(HttpContext, ct), id, request, ct);

    [HttpDelete("{id:guid}")]
    [Audit(AuditEvents.ConfigDeleted)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await config.DeleteRoleAsync(await Viewer.OfAsync(HttpContext, ct), id, ct);
        return NoContent();
    }
}

/// <summary>Sharing and ownership of maps (docs/03 §6.3), as items': manage access to the map.</summary>
[Route("api/v1/maps")]
public sealed class MapsController(MapSharingService sharing) : V1ControllerBase
{
    /// <summary>The map's shares, all of them (manage access). Returns the new list.</summary>
    [HttpPut("{id:guid}/shares")]
    [Audit(AuditEvents.MapSharesChanged)]
    public async Task<IReadOnlyList<ItemShareItem>> SetShares(Guid id, [FromBody] List<ItemShareRequest> shares, CancellationToken ct) =>
        await sharing.SetSharesAsync(await Viewer.OfAsync(HttpContext, ct), id, shares, ct);

    /// <summary>Another active user becomes the owner (manage access).</summary>
    [HttpPost("{id:guid}/owner")]
    [Audit(AuditEvents.MapOwnerChanged)]
    public async Task<IActionResult> TransferOwner(Guid id, [FromBody] ItemOwnerRequest request, CancellationToken ct)
    {
        await sharing.TransferOwnerAsync(await Viewer.OfAsync(HttpContext, ct), id, request.UserId, ct);
        return NoContent();
    }
}

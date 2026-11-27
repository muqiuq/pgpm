using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PGPM.Api.V1.Contracts;
using PGPM.Audit;
using PGPM.Auth;
using PGPM.Auth.Rights;

namespace PGPM.Api.V1;

/// <summary>Groups, their members and rights (PNMTD, docs/03 §4.7): admins only.</summary>
[Route("api/v1/groups")]
[RequireAdmin]
public sealed class GroupsController(GroupService groups) : V1ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<GroupItem>> List(CancellationToken ct) => await groups.ListAsync(ct);

    [HttpGet("{id:guid}")]
    public async Task<GroupDetail> Get(Guid id, CancellationToken ct) => await groups.GetAsync(id, ct);

    [HttpPost]
    [Audit(AuditEvents.GroupCreated)]
    public async Task<ActionResult<GroupDetail>> Create(GroupRequest request, CancellationToken ct)
    {
        var group = await groups.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = group.Id }, group);
    }

    /// <summary>Name, description, Entra app role, and the rights when the request has them (else unchanged).</summary>
    [HttpPut("{id:guid}")]
    [Audit(AuditEvents.GroupUpdated, AuditEvents.GroupRightsChanged)]
    public async Task<GroupDetail> Update(Guid id, GroupRequest request, CancellationToken ct) => await groups.UpdateAsync(id, request, ct);

    [HttpDelete("{id:guid}")]
    [Audit(AuditEvents.GroupDeleted)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await groups.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>The group's level per area, all of them (an area missing or None: nothing). Its members' next request has them.</summary>
    [HttpPut("{id:guid}/rights")]
    [Audit(AuditEvents.GroupRightsChanged)]
    public async Task<GroupDetail> Rights(Guid id, GroupRightsRequest request, CancellationToken ct) => await groups.SetRightsAsync(id, request.Rights, ct);

    /// <summary>All of the group's members. 409 membership_managed_by_entra for a group with an Entra app role.</summary>
    [HttpPut("{id:guid}/members")]
    [Audit(AuditEvents.GroupMembersChanged)]
    public async Task<GroupDetail> Members(Guid id, GroupMembersRequest request, CancellationToken ct) => await groups.SetMembersAsync(id, request, ct);
}

/// <summary>The areas and levels (docs/03 §6.2), for the group page and Your access: every signed-in user.</summary>
[Route("api/v1/rights")]
[Authorize]
public sealed class RightsController : V1ControllerBase
{
    [HttpGet]
    public IReadOnlyList<RightsAreaItem> Catalog() => GroupService.Catalog();
}

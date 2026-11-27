using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PGPM.Api.V1.Contracts;

namespace PGPM.Api.V1;

/// <summary>The application's name and version, for everyone: the shell shows it before any sign-in.</summary>
[Route("api/v1/info")]
public sealed class InfoController : V1ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    public AppInfo Get() => new(ServerInfo.Name, ServerInfo.Version);
}

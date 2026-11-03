using System.Security.Claims;
using ApexERP.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ApexERP.API.Controllers
{
    /// <summary>
    /// Base for every authenticated controller. Authorization is layered: [Authorize] here requires a valid
    /// token; the ModulePermissionConvention (or an explicit [HasPermission]) then requires the matching
    /// permission, so no endpoint is reachable merely by being signed in.
    /// </summary>
    [ApiController]
    [Authorize]
    public abstract class ApiControllerBase : ControllerBase
    {
        protected Guid CompanyId =>
            Guid.TryParse(User.FindFirst("company_id")?.Value, out var id)
                ? id
                : throw new UnauthorizedAccessException("Missing company_id claim.");

        /// <summary>The signed-in user. Reads "sub", falling back to the mapped NameIdentifier claim, so it works
        /// whether or not the JWT handler maps inbound claim types.</summary>
        protected Guid UserId =>
            Guid.TryParse((User.FindFirst("sub") ?? User.FindFirst(ClaimTypes.NameIdentifier))?.Value, out var id)
                ? id
                : throw new UnauthorizedAccessException("Missing sub claim.");

        /// <summary>Permissions granted by the caller's role (from the validated token).</summary>
        protected HashSet<string> GrantedPermissions =>
            PermissionEvaluator.Parse(User.FindAll("permissions").Select(c => c.Value));

        protected bool HasPermission(string permission) => PermissionEvaluator.Has(GrantedPermissions, permission);
    }
}

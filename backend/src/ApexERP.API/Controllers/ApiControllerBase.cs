using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ApexERP.API.Controllers
{
    [ApiController]
    [Authorize]
    public abstract class ApiControllerBase : ControllerBase
    {
        protected Guid CompanyId =>
            Guid.TryParse(User.FindFirst("company_id")?.Value, out var id)
                ? id
                : throw new UnauthorizedAccessException("Missing company_id claim.");

        protected Guid UserId =>
            Guid.TryParse(User.FindFirst("sub")?.Value, out var id)
                ? id
                : throw new UnauthorizedAccessException("Missing sub claim.");
    }
}

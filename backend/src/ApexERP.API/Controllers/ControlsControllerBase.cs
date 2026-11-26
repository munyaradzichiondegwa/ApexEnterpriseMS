using Microsoft.AspNetCore.Mvc;

namespace ApexERP.API.Controllers
{
    /// <summary>Shared by the controls/automation controllers: business-rule violations become 400 with the message (the request transaction rolls back).</summary>
    public abstract class ControlsControllerBase : ApiControllerBase
    {
        protected static async Task<IActionResult> Run(Func<Task<IActionResult>> action)
        {
            try { return await action(); }
            catch (InvalidOperationException ex) { return new BadRequestObjectResult(new { error = ex.Message }); }
        }

        protected static DateTime DateOrToday(DateTime? d) => (d ?? DateTime.UtcNow).Date;
    }
}

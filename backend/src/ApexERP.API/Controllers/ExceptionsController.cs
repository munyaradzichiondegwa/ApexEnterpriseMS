using ApexERP.API.Authorization;
using ApexERP.Application.Security;
using ApexERP.Application.Services;
using ApexERP.Domain.Common;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Controllers
{
    public record CloseExceptionRequest(string? Note, bool Dismiss = false);
    public record AssignExceptionRequest(Guid UserId);

    /// <summary>
    /// The visible exception queue: failed inter-company mirrors and other process failures that could not
    /// complete automatically. Each item has an owner, a status and a history (in the audit log).
    /// </summary>
    [Route("api/v1/exceptions")]
    public class ExceptionsController : ApiControllerBase
    {
        private readonly ExceptionQueueService _exceptions;
        private readonly InterCompanyService _interCompany;
        private readonly ApexErpDbContext _db;

        public ExceptionsController(ExceptionQueueService exceptions, InterCompanyService interCompany, ApexErpDbContext db)
        {
            _exceptions = exceptions;
            _interCompany = interCompany;
            _db = db;
        }

        [HttpGet]
        [HasPermission(Permissions.ExceptionsRead)]
        public async Task<IActionResult> List([FromQuery] ExceptionStatus? status, [FromQuery] ExceptionCategory? category) =>
            Ok(await _exceptions.ListAsync(CompanyId, status, category));

        /// <summary>Retries the failed work. For an inter-company mirror this re-attempts the counterparty posting;
        /// the response reports Completed (exception closed) or Failed (exception stays open, retry count updated).</summary>
        [HttpPost("{id}/retry")]
        [HasPermission(Permissions.ExceptionsManage)]
        public async Task<IActionResult> Retry(Guid id)
        {
            try
            {
                var item = await _exceptions.GetAsync(CompanyId, id);
                if (item.Status != ExceptionStatus.Open)
                    return BadRequest(new { error = "This exception is already closed." });
                if (item.Category != ExceptionCategory.InterCompanyMirror)
                    return BadRequest(new { error = "Automatic retry is not available for this type of exception. Resolve it manually." });

                var outcome = await _interCompany.TryMirrorAsync(CompanyId, item.SourceId, UserId);
                return Ok(outcome);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        /// <summary>Closes an exception with a mandatory note (Resolved, or Dismissed when Dismiss = true).</summary>
        [HttpPost("{id}/close")]
        [HasPermission(Permissions.ExceptionsManage)]
        public async Task<IActionResult> Close(Guid id, CloseExceptionRequest req)
        {
            try
            {
                var item = await _exceptions.CloseAsync(CompanyId, id, UserId,
                    req.Dismiss ? ExceptionStatus.Dismissed : ExceptionStatus.Resolved, req.Note ?? string.Empty);
                return Ok(item);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("{id}/assign")]
        [HasPermission(Permissions.ExceptionsManage)]
        public async Task<IActionResult> Assign(Guid id, AssignExceptionRequest req)
        {
            try
            {
                // The tenant filter scopes this lookup to the caller's company, so only a colleague can be assigned.
                if (!await _db.Users.AnyAsync(u => u.Id == req.UserId && u.IsActive))
                    return BadRequest(new { error = "That user was not found in your company." });

                return Ok(await _exceptions.AssignAsync(CompanyId, id, req.UserId, UserId));
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }
    }
}

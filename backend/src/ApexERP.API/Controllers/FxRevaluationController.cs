using ApexERP.API.Authorization;
using ApexERP.Application.Security;
using ApexERP.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace ApexERP.API.Controllers;

public sealed record FxRevaluationRequest(DateTime AsOfDate, Guid FxGainAccountId, Guid FxLossAccountId);

[Route("api/v1/gl/fx-revaluation")]
public sealed class FxRevaluationController : ApiControllerBase
{
    private readonly FxRevaluationService _service;

    public FxRevaluationController(FxRevaluationService service) => _service = service;

    [HttpGet("preview")]
    [HasPermission(Permissions.ReportsFinancialRead)]
    public async Task<IActionResult> Preview([FromQuery] DateTime asOfDate)
    {
        try { return Ok(await _service.PreviewAsync(CompanyId, asOfDate)); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("post")]
    [HasPermission(Permissions.GlJournalsPost)]
    public async Task<IActionResult> Post([FromBody] FxRevaluationRequest request)
    {
        try
        {
            var journal = await _service.PostAsync(CompanyId, request.AsOfDate, request.FxGainAccountId,
                request.FxLossAccountId, UserId);
            return Ok(journal);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }
}

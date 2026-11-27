using ApexERP.API.Authorization;
using ApexERP.Application.Security;
using ApexERP.Application.Services;
using ApexERP.Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace ApexERP.API.Controllers;

public sealed record CreateStatutoryPackRequest(string JurisdictionCode, string JurisdictionName, int Version,
    DateTime EffectiveFrom, DateTime? EffectiveTo, string RulesJson, List<StatutoryPackSource> Sources);
public sealed record UpdateStatutoryPackDraftRequest(string RulesJson, List<StatutoryPackSource> Sources);

[Route("api/v1/statutory-packs")]
public sealed class StatutoryPacksController : ApiControllerBase
{
    private readonly StatutoryPackService _packs;

    public StatutoryPacksController(StatutoryPackService packs) => _packs = packs;

    [HttpPost("seed/zw-2026")]
    [HasPermission(Permissions.ControlsGovernanceManage)]
    public async Task<IActionResult> SeedZimbabwe2026Baseline()
    {
        try { return Ok(await _packs.CreateZimbabwe2026BaselineDraftAsync(CompanyId, UserId)); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
    }

    [HttpGet]
    [HasPermission(Permissions.ControlsGovernanceManage)]
    public async Task<IActionResult> List([FromQuery] string? jurisdictionCode) =>
        Ok(await _packs.ListAsync(CompanyId, jurisdictionCode));

    [HttpGet("{jurisdictionCode}/applicable")]
    [HasPermission(Permissions.ControlsGovernanceManage)]
    public async Task<IActionResult> Applicable(string jurisdictionCode, [FromQuery] DateTime onDate)
    {
        try { return Ok(await _packs.GetApplicableAsync(CompanyId, jurisdictionCode, onDate)); }
        catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
    }

    [HttpPost]
    [HasPermission(Permissions.ControlsGovernanceManage)]
    public async Task<IActionResult> CreateDraft([FromBody] CreateStatutoryPackRequest request)
    {
        try
        {
            var pack = await _packs.CreateDraftAsync(CompanyId, request.JurisdictionCode, request.JurisdictionName,
                request.Version, request.EffectiveFrom, request.EffectiveTo, request.RulesJson, request.Sources, UserId);
            return Ok(pack);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPut("{jurisdictionCode}/{version:int}")]
    [HasPermission(Permissions.ControlsGovernanceManage)]
    public async Task<IActionResult> UpdateDraft(string jurisdictionCode, int version, [FromBody] UpdateStatutoryPackDraftRequest request)
    {
        try { return Ok(await _packs.UpdateDraftAsync(CompanyId, jurisdictionCode, version, request.RulesJson, request.Sources, UserId)); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("{jurisdictionCode}/{version:int}/review")]
    [HasPermission(Permissions.ControlsGovernanceManage)]
    public async Task<IActionResult> Review(string jurisdictionCode, int version)
    {
        try { return Ok(await _packs.ReviewAsync(CompanyId, jurisdictionCode, version, UserId)); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("{jurisdictionCode}/{version:int}/approve")]
    [HasPermission(Permissions.ControlsGovernanceApprove)]
    public async Task<IActionResult> Approve(string jurisdictionCode, int version)
    {
        try { return Ok(await _packs.ApproveAsync(CompanyId, jurisdictionCode, version, UserId)); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("{jurisdictionCode}/{version:int}/publish")]
    [HasPermission(Permissions.ControlsGovernanceApprove)]
    public async Task<IActionResult> Publish(string jurisdictionCode, int version)
    {
        try { return Ok(await _packs.PublishAsync(CompanyId, jurisdictionCode, version, UserId)); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }
}

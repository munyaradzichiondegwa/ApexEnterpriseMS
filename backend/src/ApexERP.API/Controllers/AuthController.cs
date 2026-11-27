using ApexERP.Application.Security;
using ApexERP.Infrastructure.Data;
using ApexERP.Domain.Entities;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ApexERP.API.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ApexERP.API.Controllers
{
    public record RegisterRequest(string CompanyName, string Email, string Password, string FirstName, string LastName);
    public record LoginRequest(string Email, string Password, string? TotpCode = null);
    public record RefreshRequest(string? RefreshToken = null);
    public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
    public record ConfirmMfaRequest(string Code);
    public record DisableMfaRequest(string Password, string Code);
    public record SwitchCompanyRequest(Guid CompanyId);
    public record AcceptCompanyInvitationRequest(string Token, string Email, string Password, string FirstName, string LastName);

    [ApiController]
    [Route("api/v1/auth")]
    public class AuthController : ControllerBase
    {
        public const string RefreshCookie = "apex_rt";
        private readonly AuthService _auth;
        private readonly IConfiguration _config;
        private readonly IWebHostEnvironment _env;
        private readonly ApexErpDbContext _db;

        public AuthController(AuthService auth, IConfiguration config, IWebHostEnvironment env, ApexErpDbContext db) { _auth = auth; _config = config; _env = env; _db = db; }

        // By default the refresh token lives only in an HttpOnly cookie (JavaScript can never read it) and is not returned in the body.
        private bool CookieOnly => _config.GetValue("Auth:RefreshTokenCookieOnly", true);
        private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

        private LoginResult Deliver(LoginResult r)
        {
            var sameSite = Enum.TryParse<SameSiteMode>(_config["Auth:CookieSameSite"], true, out var m) ? m : SameSiteMode.Strict;
            Response.Cookies.Append(RefreshCookie, r.RefreshToken, new CookieOptions
            {
                HttpOnly = true, Secure = !_env.IsDevelopment() || Request.IsHttps, SameSite = sameSite,
                Expires = DateTimeOffset.UtcNow.Add(AuthService.RefreshLifetime), Path = "/", IsEssential = true
            });
            return CookieOnly ? r with { RefreshToken = string.Empty } : r;
        }

        /// <summary>The refresh endpoint is state-changing and cookie-authenticated, so it also needs a header a cross-site form cannot send.</summary>
        private bool HasCsrfHeader => string.Equals(Request.Headers["X-Requested-With"], "apexerp", StringComparison.Ordinal);

        [HttpPost("register")]
        [AllowAnonymous]
        [EnableRateLimiting("auth")]
        public async Task<IActionResult> Register(RegisterRequest req)
        {
            var passwordProblem = PasswordPolicy.Validate(req.Password, req.Email);
            if (passwordProblem != null) return BadRequest(new { error = passwordProblem });
            try
            {
                var user = await _auth.RegisterCompanyAndAdminAsync(req.CompanyName, req.Email, req.Password, req.FirstName, req.LastName);
                return Ok(Deliver(await _auth.IssueTokensAsync(user, Ip, null)));
            }
            catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
        }

        [HttpPost("login")]
        [AllowAnonymous]
        [EnableRateLimiting("auth")]
        public async Task<IActionResult> Login(LoginRequest req)
        {
            try { return Ok(Deliver(await _auth.LoginAsync(req.Email, req.Password, req.TotpCode, Ip))); }
            catch (MfaRequiredException ex) { return Unauthorized(new { error = ex.Message, mfaRequired = true }); }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { error = ex.Message }); }
        }

        [HttpPost("refresh")]
        [AllowAnonymous]
        [EnableRateLimiting("auth")]
        public async Task<IActionResult> Refresh([FromBody] RefreshRequest? req)
        {
            if (!HasCsrfHeader) return BadRequest(new { error = "Missing X-Requested-With header." });
            var token = Request.Cookies[RefreshCookie] ?? (CookieOnly ? null : req?.RefreshToken);
            try { return Ok(Deliver(await _auth.RefreshAsync(token ?? "", Ip))); }
            catch (UnauthorizedAccessException ex)
            {
                Response.Cookies.Delete(RefreshCookie, new CookieOptions { Path = "/" });
                return Unauthorized(new { error = ex.Message });
            }
        }

        [HttpPost("logout")]
        [AllowAnonymous]
        public async Task<IActionResult> Logout([FromBody] RefreshRequest? req)
        {
            if (!HasCsrfHeader) return BadRequest(new { error = "Missing X-Requested-With header." });
            await _auth.LogoutAsync(Request.Cookies[RefreshCookie] ?? req?.RefreshToken);
            Response.Cookies.Delete(RefreshCookie, new CookieOptions { Path = "/" });
            return Ok(new { loggedOut = true });
        }


        [HttpPost("accept-company-invitation")]
        [AllowAnonymous]
        [EnableRateLimiting("auth")]
        public async Task<IActionResult> AcceptCompanyInvitation(AcceptCompanyInvitationRequest request, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.Token) || string.IsNullOrWhiteSpace(request.Email))
                return BadRequest(new { error = "Invitation token and email are required." });
            var passwordProblem = PasswordPolicy.Validate(request.Password, request.Email);
            if (passwordProblem != null) return BadRequest(new { error = passwordProblem });
            using var scope = _db.SystemAccessScope("onboarding: accept company user invitation");
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Token)));
            var invitation = await _db.CompanyUserInvitations.SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
            if (invitation is null || invitation.AcceptedAt.HasValue || invitation.ExpiresAt <= DateTime.UtcNow ||
                !string.Equals(invitation.Email, request.Email.Trim(), StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "Invitation is invalid, expired or already used." });
            if (await _db.Users.IgnoreQueryFilters().AnyAsync(x => x.Email == invitation.Email, ct))
                return Conflict(new { error = "An account with this email already exists." });
            var company = await _db.Companies.IgnoreQueryFilters().SingleOrDefaultAsync(
                x => x.Id == invitation.CompanyId && x.TenantId == invitation.TenantId && x.IsActive, ct);
            var tenant = await _db.CustomerOrganisations.IgnoreQueryFilters().SingleOrDefaultAsync(
                x => x.Id == invitation.TenantId && x.IsActive, ct);
            var role = await _db.Roles.IgnoreQueryFilters().SingleOrDefaultAsync(
                x => x.Id == invitation.RoleId && x.CompanyId == invitation.CompanyId, ct);
            if (company is null || tenant is null || role is null)
                return BadRequest(new { error = "The invitation's organisation, company or role is no longer available." });
            var subscription = await _db.TenantSubscriptions.Where(x => x.TenantId == tenant.Id).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
            if (subscription is null || subscription.Status is not ("Active" or "Trial") ||
                (subscription.TrialEndsAt.HasValue && subscription.TrialEndsAt.Value <= DateTime.UtcNow) ||
                (subscription.CurrentPeriodEndsAt.HasValue && subscription.CurrentPeriodEndsAt.Value <= DateTime.UtcNow))
                return StatusCode(402, new { error = "The tenant subscription is inactive or expired." });
            var plan = await _db.SubscriptionPlans.SingleAsync(x => x.Id == subscription.PlanId, ct);
            var seatLimit = subscription.SeatOverride > 0 ? subscription.SeatOverride : plan.MaxSeats;
            var activeSeats = await _db.CompanyMemberships.IgnoreQueryFilters()
                .Join(_db.Companies.IgnoreQueryFilters().Where(x => x.TenantId == tenant.Id && x.IsActive),
                    m => m.CompanyId, x => x.Id, (m,x) => new { m.UserId, m.IsActive })
                .Where(x => x.IsActive).Select(x => x.UserId).Distinct().CountAsync(ct);
            if (activeSeats >= seatLimit) return StatusCode(402, new { error = "The subscription seat limit has been reached.", seatLimit });

            var user = new User { CompanyId = company.Id, Email = invitation.Email,
                PasswordHash = AuthService.HashPassword(request.Password), FirstName = request.FirstName.Trim(),
                LastName = request.LastName.Trim(), RoleId = role.Id, IsActive = true };
            var transaction = _db.Database.CurrentTransaction;
            var ownsTransaction = transaction is null;
            if (ownsTransaction) transaction = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                _db.Users.Add(user);
                await _db.SaveChangesAsync(ct);
                _db.CompanyMemberships.Add(new CompanyMembership { CompanyId = company.Id, UserId = user.Id,
                    MembershipRole = role.Name, RoleId = role.Id, IsDefault = true,
                    IsActive = true, ValidFrom = DateTime.UtcNow });
                invitation.AcceptedAt = DateTime.UtcNow;
                invitation.AcceptedUserId = user.Id;
                await _db.SaveChangesAsync(ct);
                if (ownsTransaction) await transaction!.CommitAsync(ct);
            }
            catch
            {
                if (ownsTransaction && transaction is not null) await transaction.RollbackAsync(ct);
                throw;
            }
            finally
            {
                if (ownsTransaction && transaction is not null) await transaction.DisposeAsync();
            }
            var login = await _auth.IssueTokensAsync(user, Ip, null, ct);
            return Ok(Deliver(login));
        }

        [HttpGet("me")]
        [Authorize]
        public IActionResult Me()
        {
            var permissions = PermissionEvaluator.Parse(User.FindAll("permissions").Select(c => c.Value)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            return Ok(new
            {
                userId = (User.FindFirst("sub") ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier))?.Value,
                tenantId = User.FindFirst("tenant_id")?.Value,
                companyId = User.FindFirst("company_id")?.Value,
                email = (User.FindFirst("email") ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email))?.Value,
                role = (User.FindFirst("role") ?? User.FindFirst(System.Security.Claims.ClaimTypes.Role))?.Value,
                permissions
            });
        }


        [HttpGet("companies")]
        [Authorize]
        [AuthenticatedOnly]
        public async Task<IActionResult> MyCompanies(CancellationToken ct)
        {
            var userId = CurrentUserId;
            var homeCompanyId = Guid.Parse(User.FindFirst("company_id")!.Value);
            var tenantId = Guid.Parse(User.FindFirst("tenant_id")!.Value);
            using var scope = _db.SystemAccessScope("authentication: list companies available to signed-in user");
            var rows = await _db.Companies.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.TenantId == tenantId &&
                    (c.Id == homeCompanyId || _db.CompanyMemberships.IgnoreQueryFilters().Any(m =>
                        m.CompanyId == c.Id && m.UserId == userId && m.IsActive &&
                        (!m.ValidFrom.HasValue || m.ValidFrom <= DateTime.UtcNow) &&
                        (!m.ValidTo.HasValue || m.ValidTo > DateTime.UtcNow))))
                .OrderBy(c => c.Name)
                .Select(c => new { c.Id, c.Name, c.FunctionalCurrency, c.IsActive, IsCurrent = c.Id == homeCompanyId })
                .ToListAsync(ct);
            return Ok(rows);
        }

        [HttpPost("switch-company")]
        [Authorize]
        [AuthenticatedOnly]
        [EnableRateLimiting("auth")]
        public async Task<IActionResult> SwitchCompany(SwitchCompanyRequest request, CancellationToken ct)
        {
            try
            {
                var result = await _auth.IssueTokensForCompanyAsync(CurrentUserId, request.CompanyId, Ip, null, ct);
                return Ok(Deliver(result));
            }
            catch (UnauthorizedAccessException ex) { return Forbid(); }
        }

        private Guid CurrentUserId => Guid.Parse((User.FindFirst("sub") ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier))!.Value);

        [HttpPost("change-password")]
        [Authorize]
        [EnableRateLimiting("auth")]
        public async Task<IActionResult> ChangePassword(ChangePasswordRequest req)
        {
            try { await _auth.ChangePasswordAsync(CurrentUserId, req.CurrentPassword, req.NewPassword); Response.Cookies.Delete(RefreshCookie, new CookieOptions { Path = "/" }); return Ok(new { changed = true, signInAgain = true }); }
            catch (UnauthorizedAccessException ex) { return BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        // ---- two-step verification ----
        [HttpPost("mfa/setup")]
        [Authorize]
        public async Task<IActionResult> MfaSetup()
        {
            try { return Ok(await _auth.BeginMfaSetupAsync(CurrentUserId)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("mfa/confirm")]
        [Authorize]
        [EnableRateLimiting("auth")]
        public async Task<IActionResult> MfaConfirm(ConfirmMfaRequest req)
        {
            try { return Ok(new { enabled = true, recoveryCodes = await _auth.ConfirmMfaAsync(CurrentUserId, req.Code) }); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("mfa/disable")]
        [Authorize]
        [EnableRateLimiting("auth")]
        public async Task<IActionResult> MfaDisable(DisableMfaRequest req)
        {
            try { await _auth.DisableMfaAsync(CurrentUserId, req.Password, req.Code); return Ok(new { enabled = false }); }
            catch (UnauthorizedAccessException ex) { return BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }
    }
}

using ApexERP.Application.Security;
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

    [ApiController]
    [Route("api/v1/auth")]
    public class AuthController : ControllerBase
    {
        public const string RefreshCookie = "apex_rt";
        private readonly AuthService _auth;
        private readonly IConfiguration _config;
        private readonly IWebHostEnvironment _env;

        public AuthController(AuthService auth, IConfiguration config, IWebHostEnvironment env) { _auth = auth; _config = config; _env = env; }

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

        [HttpGet("me")]
        [Authorize]
        public IActionResult Me()
        {
            var permissions = PermissionEvaluator.Parse(User.FindAll("permissions").Select(c => c.Value)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            return Ok(new
            {
                userId = (User.FindFirst("sub") ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier))?.Value,
                companyId = User.FindFirst("company_id")?.Value,
                email = (User.FindFirst("email") ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email))?.Value,
                role = (User.FindFirst("role") ?? User.FindFirst(System.Security.Claims.ClaimTypes.Role))?.Value,
                permissions
            });
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

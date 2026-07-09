using ApexERP.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ApexERP.API.Controllers
{
    public record RegisterRequest(string CompanyName, string Email, string Password, string FirstName, string LastName);
    public record LoginRequest(string Email, string Password);

    [ApiController]
    [Route("api/v1/auth")]
    public class AuthController : ControllerBase
    {
        private readonly AuthService _auth;

        public AuthController(AuthService auth)
        {
            _auth = auth;
        }

        /// <summary>Onboarding: creates a new company (tenant) with its first Administrator user.
        /// Matches PRD Section 15 "Time to first financial statement &lt; 2 hours" onboarding goal.</summary>
        [HttpPost("register")]
        [AllowAnonymous]
        public async Task<IActionResult> Register(RegisterRequest req)
        {
            try
            {
                var user = await _auth.RegisterCompanyAndAdminAsync(req.CompanyName, req.Email, req.Password, req.FirstName, req.LastName);
                var tokens = _auth.IssueTokens(user);
                return Ok(tokens);
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new { error = ex.Message });
            }
        }

        [HttpPost("login")]
        [AllowAnonymous]
        public async Task<IActionResult> Login(LoginRequest req)
        {
            try
            {
                var result = await _auth.LoginAsync(req.Email, req.Password);
                return Ok(result);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Unauthorized(new { error = ex.Message });
            }
        }

        [HttpGet("me")]
        [Authorize]
        public IActionResult Me()
        {
            return Ok(new
            {
                userId = User.FindFirst("sub")?.Value,
                companyId = User.FindFirst("company_id")?.Value,
                email = User.FindFirst("email")?.Value,
                role = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value
            });
        }
    }
}

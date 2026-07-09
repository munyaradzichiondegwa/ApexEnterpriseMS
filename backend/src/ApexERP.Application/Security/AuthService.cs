using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace ApexERP.Application.Security
{
    public record LoginResult(string AccessToken, string RefreshToken, DateTime ExpiresAt, Guid UserId, Guid CompanyId, string Role);

    /// <summary>
    /// Handles credential verification (BCrypt-hashed passwords) and JWT issuance carrying
    /// company_id + role + permissions claims, which TenantMiddleware and [Authorize(Policy=...)]
    /// attributes use to enforce multi-tenant RBAC across every controller. Short-lived access
    /// tokens (15 min) + longer-lived refresh tokens per PRD Section 6.2.
    /// </summary>
    public class AuthService
    {
        private readonly ApexErpDbContext _db;
        private readonly string _jwtSecret;
        private readonly string _issuer;

        public AuthService(ApexErpDbContext db, string jwtSecret, string issuer)
        {
            _db = db;
            _jwtSecret = jwtSecret;
            _issuer = issuer;
        }

        public static string HashPassword(string password) => BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12);

        public static bool VerifyPassword(string password, string hash) => BCrypt.Net.BCrypt.Verify(password, hash);

        public async Task<LoginResult> LoginAsync(string email, string password, CancellationToken ct = default)
        {
            var user = await _db.Users.IgnoreQueryFilters().Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.Email == email.ToLowerInvariant() && u.IsActive, ct)
                ?? throw new UnauthorizedAccessException("Invalid email or password.");

            if (!VerifyPassword(password, user.PasswordHash))
                throw new UnauthorizedAccessException("Invalid email or password.");

            user.LastLogin = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            return IssueTokens(user);
        }

        public LoginResult IssueTokens(User user)
        {
            var expiresAt = DateTime.UtcNow.AddMinutes(15);
            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new("company_id", user.CompanyId.ToString()),
                new("email", user.Email),
                new(ClaimTypes.Role, user.Role?.Name ?? "User"),
                new("permissions", user.Role?.PermissionsJson ?? "[]")
            };

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSecret));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var token = new JwtSecurityToken(_issuer, _issuer, claims, expires: expiresAt, signingCredentials: creds);
            var accessToken = new JwtSecurityTokenHandler().WriteToken(token);

            // Refresh token: opaque random value; in production this is stored hashed in Redis with a
            // 7-day TTL and rotated/blacklisted on use (PRD 6.2). Simplified here to a signed long-lived JWT.
            var refreshExpiresAt = DateTime.UtcNow.AddDays(7);
            var refreshToken = new JwtSecurityToken(_issuer, _issuer,
                new[] { new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()), new Claim("type", "refresh") },
                expires: refreshExpiresAt, signingCredentials: creds);
            var refreshTokenStr = new JwtSecurityTokenHandler().WriteToken(refreshToken);

            return new LoginResult(accessToken, refreshTokenStr, expiresAt, user.Id, user.CompanyId, user.Role?.Name ?? "User");
        }

        public async Task<User> RegisterCompanyAndAdminAsync(string companyName, string email, string password,
            string firstName, string lastName, CancellationToken ct = default)
        {
            if (await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email.ToLowerInvariant(), ct))
                throw new InvalidOperationException("A user with this email already exists.");

            var company = new Company { Name = companyName, BaseCurrency = "USD", FunctionalCurrency = "USD" };
            _db.Companies.Add(company);

            var adminRole = new Role
            {
                CompanyId = company.Id,
                Name = "Administrator",
                IsSystemRole = true,
                PermissionsJson = "[\"*\"]"
            };
            _db.Roles.Add(adminRole);

            var user = new User
            {
                CompanyId = company.Id,
                Email = email.ToLowerInvariant(),
                PasswordHash = HashPassword(password),
                RoleId = adminRole.Id,
                FirstName = firstName,
                LastName = lastName,
                IsActive = true
            };
            _db.Users.Add(user);

            await _db.SaveChangesAsync(ct);
            return user;
        }
    }
}

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ApexERP.Domain.Controls;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace ApexERP.Application.Security
{
    public record LoginResult(string AccessToken, string RefreshToken, DateTime ExpiresAt, Guid UserId, Guid CompanyId, string Role,
        IReadOnlyList<string> Permissions);

    public record MfaSetup(string Secret, string ProvisioningUri);

    /// <summary>Thrown when the password was right but a second factor is needed. The API turns it into a 401 with mfaRequired = true.</summary>
    public class MfaRequiredException : UnauthorizedAccessException
    {
        public MfaRequiredException() : base("A verification code is required.") { }
    }

    /// <summary>
    /// Credential verification, lockout, MFA, JWT access tokens (15 min) and rotating opaque refresh tokens (7 days).
    /// Refresh tokens are random values stored only as SHA-256 hashes. Every use rotates the token; presenting an
    /// already-used token revokes the whole family (theft detection). Spec section 11.
    /// </summary>
    public class AuthService
    {
        public static string RefreshAudience(string issuer) => issuer + ".refresh";
        public static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(7);
        public const int MaxFailedAttempts = 5;

        // Verified against when no such user exists, so unknown e-mails and wrong passwords take similar time.
        private static readonly string DummyHash = HashPassword("apexerp-timing-equalisation-only");

        private readonly ApexErpDbContext _db;
        private readonly string _jwtSecret;
        private readonly string _issuer;
        private readonly SecretProtector _protector;

        public AuthService(ApexErpDbContext db, string jwtSecret, string issuer)
        {
            _db = db; _jwtSecret = jwtSecret; _issuer = issuer;
            _protector = new SecretProtector(jwtSecret);
        }

        public static string HashPassword(string password) => BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12);
        public static bool VerifyPassword(string password, string hash) => BCrypt.Net.BCrypt.Verify(password, hash);
        private static string Sha256(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

        // ---------------------------------------------------------------------------------------------
        // Sign-in
        // ---------------------------------------------------------------------------------------------
        public async Task<LoginResult> LoginAsync(string email, string password, string? totpCode = null, string? ip = null, CancellationToken ct = default)
        {
            using var systemAccess = _db.SystemAccessScope("authentication: credential lookup and token issuance");
            var normalised = (email ?? string.Empty).Trim().ToLowerInvariant();
            var now = DateTime.UtcNow;

            // Sign-in is the one place that legitimately looks across tenants, so it opts out of the tenant filter explicitly.
            var candidates = await _db.Users.IgnoreQueryFilters().Include(u => u.Role)
                .Where(u => u.Email == normalised && u.IsActive).ToListAsync(ct);

            if (candidates.Count == 0)
            {
                VerifyPassword(password ?? string.Empty, DummyHash);
                throw new UnauthorizedAccessException("Invalid email or password.");
            }

            var matches = candidates.Where(u => VerifyPassword(password ?? string.Empty, u.PasswordHash)).ToList();
            if (matches.Count == 0)
            {
                foreach (var u in candidates.Where(u => !AccountLockout.IsLocked(u.LockedUntil, now))) RegisterFailure(u, now, ip, "bad password");
                await _db.SaveChangesAsync(ct);
                throw new UnauthorizedAccessException("Invalid email or password.");
            }
            if (matches.Count > 1)
                throw new UnauthorizedAccessException("This e-mail address is registered to more than one company. Ask your administrator to resolve the duplicate account.");

            var user = matches[0];
            if (AccountLockout.IsLocked(user.LockedUntil, now))
                throw new UnauthorizedAccessException("Too many failed attempts. Try again later.");

            if (user.TotpEnabled)
            {
                if (string.IsNullOrWhiteSpace(totpCode)) throw new MfaRequiredException();
                if (!await VerifySecondFactorAsync(user, totpCode, now, ct))
                {
                    RegisterFailure(user, now, ip, "bad second factor");
                    await _db.SaveChangesAsync(ct);
                    throw new UnauthorizedAccessException("The verification code is not valid.");
                }
            }

            user.FailedLoginCount = 0; user.LockedUntil = null; user.LastLogin = now;
            Log(user, "Auth.LoginSucceeded", ip, null);
            await _db.SaveChangesAsync(ct);
            return await IssueTokensAsync(user, ip, null, ct);
        }

        private void RegisterFailure(User user, DateTime now, string? ip, string why)
        {
            user.FailedLoginCount++;
            user.LockedUntil = AccountLockout.NextLockout(user.FailedLoginCount, now, MaxFailedAttempts);
            Log(user, user.LockedUntil != null ? "Auth.AccountLocked" : "Auth.LoginFailed", ip, new { why, attempts = user.FailedLoginCount });
        }

        private void Log(User user, string action, string? ip, object? detail) =>
            _db.AuditLogs.Add(new AuditLogEntry
            {
                CompanyId = user.CompanyId, UserId = user.Id, IpAddress = ip, Action = action, Module = "Auth", EntityType = nameof(User), EntityId = user.Id,
                AfterValueJson = detail == null ? null : System.Text.Json.JsonSerializer.Serialize(detail), Timestamp = DateTime.UtcNow
            });

        private async Task<bool> VerifySecondFactorAsync(User user, string code, DateTime now, CancellationToken ct)
        {
            code = code.Trim();
            if (user.TotpSecret != null && code.Length == 6 && code.All(char.IsDigit))
                return Totp.Verify(_protector.Unprotect(user.TotpSecret), code, now);
            var hash = Totp.HashRecoveryCode(code);
            var rc = await _db.MfaRecoveryCodes.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.UserId == user.Id && r.CodeHash == hash && r.UsedAt == null, ct);
            if (rc == null) return false;
            rc.UsedAt = now;                       // a recovery code works once
            Log(user, "Auth.RecoveryCodeUsed", null, null);
            return true;
        }

        // ---------------------------------------------------------------------------------------------
        // Tokens
        // ---------------------------------------------------------------------------------------------
        public async Task<LoginResult> IssueTokensAsync(User user, string? ip, Guid? familyId, CancellationToken ct = default, Guid? targetCompanyId = null, Guid? targetRoleId = null)
        {
            using var systemAccess = _db.SystemAccessScope("authentication: tenant validation and token issuance");
            if (user.Role == null)
                user = await _db.Users.IgnoreQueryFilters().Include(u => u.Role).FirstAsync(u => u.Id == user.Id, ct);

            var effectiveCompanyId = targetCompanyId ?? user.CompanyId;
            var effectiveRole = targetRoleId.HasValue
                ? await _db.Roles.IgnoreQueryFilters().SingleOrDefaultAsync(r => r.Id == targetRoleId.Value && r.CompanyId == effectiveCompanyId, ct)
                : user.Role;
            if (effectiveRole == null) throw new UnauthorizedAccessException("The selected company role is unavailable.");

            var tenantId = await _db.Companies.IgnoreQueryFilters()
                .Where(c => c.Id == effectiveCompanyId && c.IsActive)
                .Select(c => c.TenantId)
                .SingleOrDefaultAsync(ct);
            if (!tenantId.HasValue || !await _db.CustomerOrganisations.IgnoreQueryFilters()
                    .AnyAsync(t => t.Id == tenantId.Value && t.IsActive, ct))
                throw new InvalidOperationException("The customer's tenant is missing or inactive. Contact the platform administrator.");

            var expiresAt = DateTime.UtcNow.AddMinutes(15);
            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new("tenant_id", tenantId.Value.ToString()),
                new("company_id", effectiveCompanyId.ToString()),
                new("email", user.Email),
                new(ClaimTypes.Role, effectiveRole.Name),
                new("role_id", effectiveRole.Id.ToString()),
                new("permissions", effectiveRole.PermissionsJson ?? "[]")
            };
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSecret));
            var token = new JwtSecurityToken(_issuer, _issuer, claims, expires: expiresAt, signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
            var accessToken = new JwtSecurityTokenHandler().WriteToken(token);

            var opaque = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            _db.RefreshTokens.Add(new RefreshToken
            {
                CompanyId = effectiveCompanyId, UserId = user.Id, FamilyId = familyId ?? Guid.NewGuid(), TokenHash = Sha256(opaque),
                ExpiresAt = DateTime.UtcNow.Add(RefreshLifetime), CreatedByIp = ip
            });
            await _db.SaveChangesAsync(ct);

            var permissions = PermissionEvaluator.Parse(effectiveRole.PermissionsJson).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            return new LoginResult(accessToken, opaque, expiresAt, user.Id, effectiveCompanyId, effectiveRole.Name, permissions);
        }


        /// <summary>Issues a company-scoped session only for the user's primary company or an active tenant membership.</summary>
        public async Task<LoginResult> IssueTokensForCompanyAsync(Guid userId, Guid companyId, string? ip,
            Guid? familyId = null, CancellationToken ct = default)
        {
            using var systemAccess = _db.SystemAccessScope("authentication: validate company membership and switch context");
            var user = await _db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == userId && x.IsActive, ct);
            if (user is null) throw new UnauthorizedAccessException("User is inactive or unavailable.");
            Guid? roleId = null;
            if (user.CompanyId != companyId)
            {
                var membership = await _db.CompanyMemberships.IgnoreQueryFilters().SingleOrDefaultAsync(x =>
                    x.UserId == userId && x.CompanyId == companyId && x.IsActive &&
                    (!x.ValidFrom.HasValue || x.ValidFrom <= DateTime.UtcNow) &&
                    (!x.ValidTo.HasValue || x.ValidTo > DateTime.UtcNow), ct);
                if (membership is null || !membership.RoleId.HasValue)
                    throw new UnauthorizedAccessException("You do not have active access to this company.");
                roleId = membership.RoleId;
            }
            else roleId = user.RoleId;

            var targetTenant = await _db.Companies.IgnoreQueryFilters()
                .Where(x => x.Id == companyId && x.IsActive).Select(x => x.TenantId).SingleOrDefaultAsync(ct);
            var homeTenant = await _db.Companies.IgnoreQueryFilters()
                .Where(x => x.Id == user.CompanyId).Select(x => x.TenantId).SingleOrDefaultAsync(ct);
            if (!targetTenant.HasValue || targetTenant != homeTenant ||
                !await _db.CustomerOrganisations.IgnoreQueryFilters().AnyAsync(x => x.Id == targetTenant.Value && x.IsActive, ct))
                throw new UnauthorizedAccessException("Company access must remain within the user's active tenant.");

            user.Role = null;
            return await IssueTokensAsync(user, ip, familyId, ct, companyId, roleId);
        }

        /// <summary>Exchanges a refresh token for a new access token and a new refresh token (rotation).</summary>
        public async Task<LoginResult> RefreshAsync(string refreshToken, string? ip, CancellationToken ct = default)
        {
            using var systemAccess = _db.SystemAccessScope("authentication: refresh-token rotation");
            if (string.IsNullOrWhiteSpace(refreshToken)) throw new UnauthorizedAccessException("Session expired.");
            var hash = Sha256(refreshToken);
            var stored = await _db.RefreshTokens.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
            if (stored == null) throw new UnauthorizedAccessException("Session expired.");
            var now = DateTime.UtcNow;

            if (stored.RevokedAt != null)
            {
                // A token that was already used is being presented again: assume theft and end every session in the family.
                await RevokeFamilyAsync(stored.FamilyId, "refresh token reuse detected", ct);
                var u = await _db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == stored.UserId, ct);
                if (u != null) { Log(u, "Auth.RefreshReuseDetected", ip, new { stored.FamilyId }); await _db.SaveChangesAsync(ct); }
                throw new UnauthorizedAccessException("Session expired.");
            }
            if (stored.ExpiresAt <= now) throw new UnauthorizedAccessException("Session expired.");

            var user = await _db.Users.IgnoreQueryFilters().Include(x => x.Role).FirstOrDefaultAsync(x => x.Id == stored.UserId, ct);
            if (user == null || !user.IsActive || AccountLockout.IsLocked(user.LockedUntil, now))
            { await RevokeFamilyAsync(stored.FamilyId, "user inactive or locked", ct); throw new UnauthorizedAccessException("Session expired."); }
            if (user.PasswordChangedAt != null && user.PasswordChangedAt > stored.CreatedAt)
            { await RevokeFamilyAsync(stored.FamilyId, "password changed", ct); throw new UnauthorizedAccessException("Session expired."); }

            var result = await IssueTokensForCompanyAsync(user.Id, stored.CompanyId, ip, stored.FamilyId, ct);
            stored.RevokedAt = now; stored.RevokedReason = "rotated"; stored.ReplacedByTokenHash = Sha256(result.RefreshToken);
            await _db.SaveChangesAsync(ct);
            return result;
        }

        public async Task LogoutAsync(string? refreshToken, CancellationToken ct = default)
        {
            using var systemAccess = _db.SystemAccessScope("authentication: refresh-token logout");
            if (string.IsNullOrWhiteSpace(refreshToken)) return;
            var hash = Sha256(refreshToken);
            var stored = await _db.RefreshTokens.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
            if (stored != null) await RevokeFamilyAsync(stored.FamilyId, "logout", ct);
        }

        public async Task RevokeAllForUserAsync(Guid userId, string reason, CancellationToken ct = default)
        {
            var live = await _db.RefreshTokens.IgnoreQueryFilters().Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct);
            foreach (var t in live) { t.RevokedAt = DateTime.UtcNow; t.RevokedReason = reason; }
            await _db.SaveChangesAsync(ct);
        }

        private async Task RevokeFamilyAsync(Guid familyId, string reason, CancellationToken ct)
        {
            var live = await _db.RefreshTokens.IgnoreQueryFilters().Where(t => t.FamilyId == familyId && t.RevokedAt == null).ToListAsync(ct);
            foreach (var t in live) { t.RevokedAt = DateTime.UtcNow; t.RevokedReason = reason; }
            await _db.SaveChangesAsync(ct);
        }

        // ---------------------------------------------------------------------------------------------
        // Password and MFA management (signed-in user)
        // ---------------------------------------------------------------------------------------------
        public async Task ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken ct = default)
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new InvalidOperationException("User not found.");
            if (!VerifyPassword(currentPassword ?? "", user.PasswordHash)) throw new UnauthorizedAccessException("The current password is not correct.");
            var problem = PasswordPolicy.Validate(newPassword, user.Email);
            if (problem != null) throw new InvalidOperationException(problem);
            user.PasswordHash = HashPassword(newPassword); user.PasswordChangedAt = DateTime.UtcNow;
            Log(user, "Auth.PasswordChanged", null, null);
            await _db.SaveChangesAsync(ct);
            await RevokeAllForUserAsync(userId, "password changed", ct);      // every existing session ends
        }

        public async Task<MfaSetup> BeginMfaSetupAsync(Guid userId, CancellationToken ct = default)
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new InvalidOperationException("User not found.");
            if (user.TotpEnabled) throw new InvalidOperationException("Two-step verification is already on. Turn it off first to enrol a new device.");
            var secret = Totp.GenerateSecret();
            user.TotpSecret = _protector.Protect(secret);      // not active until confirmed
            await _db.SaveChangesAsync(ct);
            return new MfaSetup(secret, Totp.ProvisioningUri("ApexERP", user.Email, secret));
        }

        /// <summary>Confirms the first code from the authenticator app, turns MFA on and returns one-time recovery codes (shown once).</summary>
        public async Task<IReadOnlyList<string>> ConfirmMfaAsync(Guid userId, string code, CancellationToken ct = default)
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new InvalidOperationException("User not found.");
            if (user.TotpSecret == null) throw new InvalidOperationException("Start MFA setup first.");
            if (!Totp.Verify(_protector.Unprotect(user.TotpSecret), code, DateTime.UtcNow)) throw new InvalidOperationException("That code is not valid. Check the time on your phone and try again.");
            user.TotpEnabled = true;
            foreach (var old in await _db.MfaRecoveryCodes.Where(r => r.UserId == userId).ToListAsync(ct)) _db.MfaRecoveryCodes.Remove(old);
            var codes = Totp.GenerateRecoveryCodes();
            foreach (var c in codes) _db.MfaRecoveryCodes.Add(new MfaRecoveryCode { CompanyId = user.CompanyId, UserId = userId, CodeHash = Totp.HashRecoveryCode(c) });
            Log(user, "Auth.MfaEnabled", null, null);
            await _db.SaveChangesAsync(ct);
            return codes;
        }

        public async Task DisableMfaAsync(Guid userId, string password, string code, CancellationToken ct = default)
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new InvalidOperationException("User not found.");
            if (!VerifyPassword(password ?? "", user.PasswordHash)) throw new UnauthorizedAccessException("The password is not correct.");
            if (user.TotpEnabled && !await VerifySecondFactorAsync(user, code ?? "", DateTime.UtcNow, ct)) throw new UnauthorizedAccessException("The verification code is not valid.");
            user.TotpEnabled = false; user.TotpSecret = null;
            foreach (var old in await _db.MfaRecoveryCodes.Where(r => r.UserId == userId).ToListAsync(ct)) _db.MfaRecoveryCodes.Remove(old);
            Log(user, "Auth.MfaDisabled", null, null);
            await _db.SaveChangesAsync(ct);
        }

        /// <summary>Administrator reset for a user who lost both their device and recovery codes. Audited; the user must re-enrol.</summary>
        public async Task AdminResetMfaAsync(Guid companyId, Guid targetUserId, Guid adminId, CancellationToken ct = default)
        {
            if (targetUserId == adminId) throw new InvalidOperationException("Use the normal flow to change your own MFA.");
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == targetUserId && u.CompanyId == companyId, ct) ?? throw new InvalidOperationException("User not found.");
            user.TotpEnabled = false; user.TotpSecret = null;
            foreach (var old in await _db.MfaRecoveryCodes.Where(r => r.UserId == targetUserId).ToListAsync(ct)) _db.MfaRecoveryCodes.Remove(old);
            _db.AuditLogs.Add(new AuditLogEntry { CompanyId = companyId, UserId = adminId, Action = "Auth.MfaResetByAdmin", Module = "Auth", EntityType = nameof(User), EntityId = targetUserId, Timestamp = DateTime.UtcNow });
            await _db.SaveChangesAsync(ct);
            await RevokeAllForUserAsync(targetUserId, "MFA reset by administrator", ct);
        }

        public async Task<User> RegisterCompanyAndAdminAsync(string companyName, string email, string password, string firstName, string lastName, CancellationToken ct = default)
        {
            using var systemAccess = _db.SystemAccessScope("registration: global email uniqueness and tenant provisioning");
            var normalisedEmail = (email ?? string.Empty).Trim().ToLowerInvariant();
            if (await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == normalisedEmail, ct))
                throw new InvalidOperationException("A user with this email already exists.");

            var tenant = new CustomerOrganisation { Name = companyName, LegalName = companyName };
            var company = new Company
            {
                TenantId = tenant.Id,
                Tenant = tenant,
                Name = companyName,
                BaseCurrency = "USD",
                FunctionalCurrency = "USD"
            };
            _db.CustomerOrganisations.Add(tenant);
            _db.Companies.Add(company);
            var adminRole = new Role { CompanyId = company.Id, Name = "Administrator", IsSystemRole = true, PermissionsJson = "[\"*\"]" };
            _db.Roles.Add(adminRole);
            var user = new User
            {
                CompanyId = company.Id, Email = normalisedEmail, PasswordHash = HashPassword(password), RoleId = adminRole.Id,
                FirstName = firstName, LastName = lastName, IsActive = true
            };
            _db.Users.Add(user);
            _db.CompanyMemberships.Add(new CompanyMembership { CompanyId = company.Id, UserId = user.Id, MembershipRole = "Administrator", RoleId = adminRole.Id, IsDefault = true, IsActive = true, ValidFrom = DateTime.UtcNow });
            await _db.SaveChangesAsync(ct);
            return user;
        }
    }
}

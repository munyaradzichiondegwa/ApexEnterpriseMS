using System.Text.Json;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Middleware;

/// <summary>Enforces subscription state and module entitlements on every tenant API request.</summary>
public sealed class SubscriptionEntitlementMiddleware
{
    private readonly RequestDelegate _next;
    public SubscriptionEntitlementMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ApexErpDbContext db)
    {
        if (context.User.Identity?.IsAuthenticated != true ||
            context.User.FindFirst("platform_admin")?.Value == "true" ||
            context.Request.Path.StartsWithSegments("/health") ||
            context.Request.Path.StartsWithSegments("/api/v1/auth") ||
            context.Request.Path.StartsWithSegments("/api/v1/organisation/subscription"))
        {
            await _next(context);
            return;
        }

        if (!Guid.TryParse(context.User.FindFirst("tenant_id")?.Value, out var tenantId))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using (db.SystemAccessScope("subscription enforcement: resolve tenant entitlement"))
        {
            var subscription = await db.TenantSubscriptions.AsNoTracking()
                .Where(x => x.TenantId == tenantId)
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync(context.RequestAborted);
            if (subscription is null || subscription.Status is not ("Active" or "Trial") ||
                (subscription.TrialEndsAt.HasValue && subscription.TrialEndsAt.Value <= DateTime.UtcNow) ||
                (subscription.CurrentPeriodEndsAt.HasValue && subscription.CurrentPeriodEndsAt.Value <= DateTime.UtcNow))
            {
                context.Response.StatusCode = StatusCodes.Status402PaymentRequired;
                await context.Response.WriteAsJsonAsync(new { error = "The tenant subscription is inactive or expired.", code = "subscription_inactive" }, context.RequestAborted);
                return;
            }

            var plan = await db.SubscriptionPlans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == subscription.PlanId && x.IsActive, context.RequestAborted);
            if (plan is null)
            {
                context.Response.StatusCode = StatusCodes.Status402PaymentRequired;
                await context.Response.WriteAsJsonAsync(new { error = "The tenant subscription plan is unavailable.", code = "plan_unavailable" }, context.RequestAborted);
                return;
            }

            var companyLimit = subscription.CompanyOverride > 0 ? subscription.CompanyOverride : plan.MaxCompanies;
            var seatLimit = subscription.SeatOverride > 0 ? subscription.SeatOverride : plan.MaxSeats;
            var activeCompanies = await db.Companies.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId && x.IsActive, context.RequestAborted);
            var activeSeats = await db.CompanyMemberships.IgnoreQueryFilters()
                .Join(db.Companies.IgnoreQueryFilters().Where(x => x.TenantId == tenantId && x.IsActive),
                    membership => membership.CompanyId, company => company.Id, (membership, company) => new { membership.UserId, membership.IsActive })
                .Where(x => x.IsActive).Select(x => x.UserId).Distinct().CountAsync(context.RequestAborted);

            var segments = context.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
            var version = Array.FindIndex(segments, x => x.Length > 1 && x[0] == 'v' && x.Skip(1).All(char.IsDigit));
            var module = version >= 0 && version + 1 < segments.Length ? segments[version + 1].ToLowerInvariant() : null;
            if (module != "organisation" && (activeCompanies > companyLimit || activeSeats > seatLimit))
            {
                context.Response.StatusCode = StatusCodes.Status402PaymentRequired;
                await context.Response.WriteAsJsonAsync(new { error = "Tenant usage exceeds the licensed company or seat limit. Manage memberships or upgrade the plan.", code = "license_limit_exceeded", activeCompanies, companyLimit, activeSeats, seatLimit }, context.RequestAborted);
                return;
            }

            if (module is not null && module is not ("organisation" or "platform"))
            {
                string[] modules;
                try { modules = JsonSerializer.Deserialize<string[]>(plan.ModuleCodesJson) ?? Array.Empty<string>(); }
                catch { modules = Array.Empty<string>(); }
                if (!modules.Contains("*", StringComparer.OrdinalIgnoreCase) &&
                    !modules.Contains(module, StringComparer.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new { error = "This module is not included in the tenant subscription.", code = "module_not_entitled", module }, context.RequestAborted);
                    return;
                }
            }
        }

        await _next(context);
    }
}

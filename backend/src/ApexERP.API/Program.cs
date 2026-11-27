using System.Text;
using System.Threading.RateLimiting;
using ApexERP.API.Authorization;
using ApexERP.API.Extensions;
using ApexERP.API.Filters;
using ApexERP.Application.Security;
using ApexERP.Application.Services;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// ---- Security-critical configuration: validated up front, with NO fallback values in code. ----
// A missing, weak or placeholder JWT secret (or an unset production origin / database) stops the
// application from starting outside Development instead of silently running with a known key.
var isDevelopment = builder.Environment.IsDevelopment();
var applyMigrationsOnStartup = builder.Configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup");
var jwtSecret = builder.Configuration["Jwt:Secret"];
var jwtIssuer = builder.Configuration["Jwt:Issuer"];
var connectionString = builder.Configuration.GetConnectionString("Default");
var frontendOrigins = builder.Configuration["Frontend:Origin"];

var configProblems = StartupConfigValidator.Validate(
    new StartupConfig(jwtSecret, jwtIssuer, connectionString, frontendOrigins), isDevelopment);

if (configProblems.Count > 0)
{
    throw new InvalidOperationException(
        "ApexERP cannot start because its configuration is not safe:" + Environment.NewLine +
        string.Join(Environment.NewLine, configProblems.Select(p => " - " + p)) + Environment.NewLine +
        "Provide these through environment variables or a secret store (see README, \"Configuration & secrets\").");
}

// ---- Database ----
// Optional PostgreSQL row-level security: see backend/database/hardening/002_row_level_security.sql and docs/OPERATIONS.md.
TenantSession.Enabled = builder.Configuration.GetValue<bool>("Security:RowLevelSecurity:Enabled");
if (applyMigrationsOnStartup && !isDevelopment)
    throw new InvalidOperationException("Automatic schema migration is permitted only in Development. Use a separate deployment migration identity in other environments.");
if (applyMigrationsOnStartup && TenantSession.Enabled)
    throw new InvalidOperationException("An RLS-enabled API must use a non-owner runtime database role and must not apply migrations at startup.");
builder.Services.AddDbContext<ApexErpDbContext>(opts => opts.UseNpgsql(connectionString!).AddInterceptors(new TenantSessionInterceptor()));

// ---- Controls (manual journal approval policy) ----
builder.Services.AddSingleton(new JournalControlOptions
{
    AllowSelfApproval = builder.Configuration.GetValue<bool>("Controls:JournalApproval:AllowSelfApproval")
});

// ---- Application services (all scoped: one DbContext per request) ----
builder.Services.AddScoped<GlPostingService>();
builder.Services.AddScoped<ArService>();
builder.Services.AddScoped<ApService>();
builder.Services.AddScoped<InventoryService>();
builder.Services.AddScoped<FixedAssetService>();
builder.Services.AddScoped<PayrollService>();
builder.Services.AddScoped<TaxComplianceService>();
builder.Services.AddScoped<ReportingService>();
builder.Services.AddScoped<ManufacturingService>();
builder.Services.AddScoped<SupplyChainService>();
builder.Services.AddScoped<CrmService>();
builder.Services.AddScoped<ProjectService>();
builder.Services.AddScoped<AuditService>();

// Full IFRS statement suite (Necessary Financial Reports 3, 4, 5)
builder.Services.AddScoped<StatementOfChangesInEquityService>();
builder.Services.AddScoped<CashFlowStatementService>();
builder.Services.AddScoped<NotesToFinancialStatementsService>();
builder.Services.AddScoped<DividendService>();

// Group / Inter-Company / Consolidation (Module 2)
builder.Services.AddScoped<GroupScopeService>();
builder.Services.AddScoped<InterCompanyService>();
builder.Services.AddScoped<ConsolidationService>();
builder.Services.AddScoped<ExceptionQueueService>();

// Public Sector & Government (Module 16)
builder.Services.AddScoped<PublicSectorService>();
builder.Services.AddScoped<TenderService>();
builder.Services.AddScoped<AssetVerificationService>();
builder.Services.AddScoped<ApprovalChainService>();

// Grants, Donors & NGO Management (Module 15)
builder.Services.AddScoped<GrantService>();

// ZW Tax Compliance & E-Invoicing / FDMS (Module 9)
builder.Services.AddScoped<FdmsService>();

// ---- Controls, automation and integrations (APEXERP spec) ----
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<GovernanceService>();
builder.Services.AddScoped<ControlMonitoringService>();
builder.Services.AddScoped<AccrualService>();
builder.Services.AddScoped<PrepaymentService>();
builder.Services.AddScoped<RevenueService>();
builder.Services.AddScoped<ImpairmentService>();
builder.Services.AddScoped<InventoryValuationService>();
builder.Services.AddScoped<ReceivablesControlService>();
builder.Services.AddScoped<ApMatchService>();
builder.Services.AddScoped<ApControlsService>();
builder.Services.AddScoped<ReconciliationService>();
builder.Services.AddScoped<PeriodCloseService>();
builder.Services.AddScoped<EvidenceService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<StatutoryRateService>();
builder.Services.AddScoped<IntegrationService>();
builder.Services.AddHostedService<ApexERP.API.Jobs.ScheduledJobRunner>();

builder.Services.AddScoped(sp =>
    new AuthService(
        sp.GetRequiredService<ApexErpDbContext>(),
        jwtSecret!,
        jwtIssuer!));

// ---- Auth ----
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
    {
        // Keep claim names exactly as issued ("sub", "email", "role", ...)
        // instead of remapping them to long URI names.
        opts.MapInboundClaims = false;

        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,

            // Access tokens use the issuer as audience. Refresh tokens use a different
            // audience, so they are rejected here and cannot be used as Bearer access tokens.
            ValidateAudience = true,
            ValidAudience = jwtIssuer,

            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtSecret!)),

            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),

            NameClaimType = "sub",
            RoleClaimType = "role"
        };

        opts.Events = new JwtBearerEvents
        {
            // A token must carry a valid tenant and must not be a refresh token.
            OnTokenValidated = context =>
            {
                var principal = context.Principal;

                var isRefresh =
                    principal?.FindFirst("token_use")?.Value == "refresh";

                var hasCompany = Guid.TryParse(principal?.FindFirst("company_id")?.Value, out _);
                var hasTenant = Guid.TryParse(principal?.FindFirst("tenant_id")?.Value, out _);

                var isPlatformAdmin = principal?.FindFirst("platform_admin")?.Value == "true";
                if (isRefresh || (!isPlatformAdmin && (!hasTenant || !hasCompany)))
                {
                    context.Fail("The token is not valid for API access.");
                }

                return Task.CompletedTask;
            }
        };
    });

// Permission-based authorization.
// Policies are created on demand and evaluated against the "permissions" claim.
// ModulePermissionConvention makes every endpoint deny-by-default.
builder.Services.AddAuthorization(options =>
{
    // Reserved for a separately governed platform identity; tenant SUPER_ADMIN is not equivalent.
    options.AddPolicy("PlatformAdmin", policy => policy.RequireAuthenticatedUser().RequireClaim("platform_admin", "true"));
});

builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

// ---- CORS: the web app origin(s). ----
var allowedOrigins =
    StartupConfigValidator.SplitOrigins(frontendOrigins).ToArray();

if (allowedOrigins.Length == 0)
{
    allowedOrigins = new[] { "http://localhost:3000" };
}

builder.Services.AddCors(opts =>
    opts.AddPolicy("Frontend", policy =>
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()));

// ---- Rate limiting (APEXERP spec 11 brute-force protection, 27 API rate limiting) ----
// "auth" is applied to sign-in and registration (per client IP). Everything else shares a generous global
// limit per signed-in user (or per IP when anonymous). Behind a reverse proxy, configure forwarded headers so the
// client IP, not the proxy's, is what is limited.
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // Provider callbacks carry no user; they are limited per client IP and authenticated by their signature.
    limiter.AddPolicy("webhook", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.Identity?.IsAuthenticated == true
            ? "u:" + (ctx.User.FindFirst("sub")?.Value ?? "?")
            : "ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.AddApexControllers();

builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc(
        "v1",
        new Microsoft.OpenApi.Models.OpenApiInfo
        {
            Title = "ApexERP API",
            Version = "v1"
        });

    c.AddSecurityDefinition(
        "Bearer",
        new Microsoft.OpenApi.Models.OpenApiSecurityScheme
        {
            In = Microsoft.OpenApi.Models.ParameterLocation.Header,
            Description = "JWT Authorization header using the Bearer scheme.",
            Name = "Authorization",
            Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
            Scheme = "Bearer"
        });

    c.AddSecurityRequirement(
        new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
        {
            {
                new Microsoft.OpenApi.Models.OpenApiSecurityScheme
                {
                    Reference = new Microsoft.OpenApi.Models.OpenApiReference
                    {
                        Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                        Id = "Bearer"
                    }
                },
                Array.Empty<string>()
            }
        });
});

// Behind a reverse proxy (Vercel, nginx, a load balancer) the connection's address is the proxy's. Set Security:TrustForwardedHeaders=true
// ONLY when every request really arrives through a proxy you control, so the real client IP is used for rate limiting and the audit trail.
if (builder.Configuration.GetValue<bool>("Security:TrustForwardedHeaders"))
{
    builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
        o.KnownNetworks.Clear();
        o.KnownProxies.Clear();
    });
}

var app = builder.Build();
if (builder.Configuration.GetValue<bool>("Security:TrustForwardedHeaders")) app.UseForwardedHeaders();

if (isDevelopment)
{
    app.Logger.LogWarning(
        "Running in Development: development-only configuration is allowed. " +
        "Never deploy with ASPNETCORE_ENVIRONMENT=Development.");
}
else
{
    // No stack traces to clients in production.
    // A missing claim is a 401; anything else is a generic 500 with a trace ID.
    app.UseExceptionHandler(errorApp =>
        errorApp.Run(async context =>
        {
            var error =
                context.Features.Get<IExceptionHandlerFeature>()?.Error;

            var status =
                error is UnauthorizedAccessException
                    ? StatusCodes.Status401Unauthorized
                    : StatusCodes.Status500InternalServerError;

            context.Response.StatusCode = status;

            await context.Response.WriteAsJsonAsync(new
            {
                title = status == StatusCodes.Status401Unauthorized
                    ? "Unauthorized"
                    : "An unexpected error occurred.",
                status,
                traceId = context.TraceIdentifier
            });
        }));
}

// An RLS-enabled runtime must use a least-privilege database role, not a schema owner.
// Validate this before serving requests. Database migrations are a separate deployment operation.
if (TenantSession.Enabled)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApexErpDbContext>();
    await TenantSession.ValidateRuntimeRoleAsync(db);
}

// Automatic migrations are an opt-in convenience for local Development only.
// Production and RLS-enabled environments must apply migrations through a separate deployment identity.
if (applyMigrationsOnStartup)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApexErpDbContext>();
    db.Database.Migrate();
}

// Swagger is a development tool; in other environments it is opt-in.
if (isDevelopment || builder.Configuration.GetValue<bool>("Swagger:Enabled"))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// ---- Security headers on every API response (APEXERP spec 29.1) ----
if (!isDevelopment) app.UseHsts();
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cache-Control"] = "no-store";
        return Task.CompletedTask;
    });
    await next();
});

app.UseMiddleware<ApexERP.API.Middleware.CorrelationIdMiddleware>();

// The client IP is recorded on every audit row, signed in or not (spec 12.2).
app.Use(async (context, next) =>
{
    context.RequestServices.GetRequiredService<ApexErpDbContext>().CurrentIpAddress = context.Connection.RemoteIpAddress?.ToString();
    await next();
});

app.UseCors("Frontend");
app.UseAuthentication();

// ---- Tenant resolution middleware ----
// The signed tenant_id and company_id claims must describe a real, active
// company belonging to an active customer organisation. Never trust a tenant
// identifier supplied in a request header or query string.
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true && context.User.FindFirst("platform_admin")?.Value != "true")
    {
        if (!Guid.TryParse(context.User.FindFirst("company_id")?.Value, out var companyId) ||
            !Guid.TryParse(context.User.FindFirst("tenant_id")?.Value, out var tenantId))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var dbContext = context.RequestServices.GetRequiredService<ApexErpDbContext>();
        bool tenantIsActive;
        using (dbContext.SystemAccessScope("tenant resolution: validate signed token identities"))
        {
            var companyBelongsToTenant = await dbContext.Companies.IgnoreQueryFilters()
                .AnyAsync(c => c.Id == companyId && c.TenantId == tenantId && c.IsActive, context.RequestAborted);
            tenantIsActive = companyBelongsToTenant &&
                await dbContext.CustomerOrganisations.IgnoreQueryFilters()
                    .AnyAsync(t => t.Id == tenantId && t.IsActive, context.RequestAborted);
        }

        if (!tenantIsActive)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        dbContext.CurrentTenantId = tenantId;
        dbContext.CurrentCompanyId = companyId;
        dbContext.CurrentIpAddress = context.Connection.RemoteIpAddress?.ToString();
        await TenantSession.RefreshAsync(dbContext, context.RequestAborted);

        if (Guid.TryParse(
                (context.User.FindFirst("sub") ??
                 context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier))?.Value,
                out var userId))
        {
            dbContext.CurrentUserId = userId;
        }
    }

    await next();
});

app.UseMiddleware<ApexERP.API.Middleware.SubscriptionEntitlementMiddleware>();

app.UseRateLimiter();   // after authentication so the global limit can key on the signed-in user
app.UseAuthorization();

app.MapControllers();

// Readiness: the API is only "ready" when it can reach its database (use this for load-balancer / orchestrator checks).
app.MapGet("/health/ready", async (ApexErpDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new { status = "ready", timestamp = DateTime.UtcNow })
        : Results.Json(new { status = "database unreachable", timestamp = DateTime.UtcNow }, statusCode: StatusCodes.Status503ServiceUnavailable));

app.MapGet(
    "/health",
    () => Results.Ok(new
    {
        status = "healthy",
        service = "ApexERP.API",
        timestamp = DateTime.UtcNow
    }));

app.Run();
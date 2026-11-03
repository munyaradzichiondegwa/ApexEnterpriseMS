using System.Text;
using ApexERP.API.Authorization;
using ApexERP.API.Extensions;
using ApexERP.API.Filters;
using ApexERP.Application.Security;
using ApexERP.Application.Services;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// ---- Security-critical configuration: validated up front, with NO fallback values in code. ----
// A missing, weak or placeholder JWT secret (or an unset production origin / database) stops the
// application from starting outside Development instead of silently running with a known key.
var isDevelopment = builder.Environment.IsDevelopment();
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
builder.Services.AddDbContext<ApexErpDbContext>(opts => opts.UseNpgsql(connectionString!));

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

                var hasTenant =
                    Guid.TryParse(
                        principal?.FindFirst("company_id")?.Value,
                        out _);

                if (isRefresh || !hasTenant)
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
builder.Services.AddAuthorization();

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

var app = builder.Build();

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

// ---- Apply pending EF Core migrations on startup. ----
// Every environment applies the same versioned database schema on boot.
using (var scope = app.Services.CreateScope())
{
    var db =
        scope.ServiceProvider.GetRequiredService<ApexErpDbContext>();

    db.Database.Migrate();
}

// Swagger is a development tool; in other environments it is opt-in.
if (isDevelopment || builder.Configuration.GetValue<bool>("Swagger:Enabled"))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("Frontend");
app.UseAuthentication();

// ---- Tenant resolution middleware ----
// Reads company_id and user ID from the validated JWT.
// Every EF Core global query filter scopes data to the caller's company.
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true)
    {
        if (!Guid.TryParse(
                context.User.FindFirst("company_id")?.Value,
                out var companyId))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var dbContext =
            context.RequestServices.GetRequiredService<ApexErpDbContext>();

        dbContext.CurrentCompanyId = companyId;

        if (Guid.TryParse(
                (context.User.FindFirst("sub") ??
                 context.User.FindFirst(
                     System.Security.Claims.ClaimTypes.NameIdentifier))?.Value,
                out var userId))
        {
            dbContext.CurrentUserId = userId;
        }
    }

    await next();
});

app.UseAuthorization();

app.MapControllers();

app.MapGet(
    "/health",
    () => Results.Ok(new
    {
        status = "healthy",
        service = "ApexERP.API",
        timestamp = DateTime.UtcNow
    }));

app.Run();
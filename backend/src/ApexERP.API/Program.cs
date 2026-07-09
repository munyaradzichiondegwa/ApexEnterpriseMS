using System.Security.Claims;
using System.Text;
using ApexERP.Application.Security;
using ApexERP.Application.Services;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

var jwtSecret = builder.Configuration["Jwt:Secret"] ?? "CHANGE_ME_IN_PRODUCTION_this_is_a_dev_only_secret_key_32chars+";
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "ApexERP";
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Host=localhost;Port=5432;Database=apexerp;Username=apexerp;Password=apexerp_dev_password";

// ---- Database ----
builder.Services.AddDbContext<ApexErpDbContext>(opts => opts.UseNpgsql(connectionString));

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
builder.Services.AddScoped<InterCompanyService>();
builder.Services.AddScoped<ConsolidationService>();
// Public Sector & Government (Module 16)
builder.Services.AddScoped<PublicSectorService>();
builder.Services.AddScoped<TenderService>();
builder.Services.AddScoped<AssetVerificationService>();
builder.Services.AddScoped<ApprovalChainService>();
// Grants, Donors & NGO Management (Module 15)
builder.Services.AddScoped<GrantService>();
// ZW Tax Compliance & E-Invoicing / FDMS (Module 9)
builder.Services.AddScoped<FdmsService>();
builder.Services.AddScoped(sp => new AuthService(sp.GetRequiredService<ApexErpDbContext>(), jwtSecret, jwtIssuer));

// ---- Auth ----
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
    {
        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtIssuer,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });
builder.Services.AddAuthorization();

// ---- CORS: Next.js frontend origin ----
builder.Services.AddCors(opts => opts.AddPolicy("Frontend", policy =>
    policy.WithOrigins(builder.Configuration["Frontend:Origin"] ?? "http://localhost:3000")
          .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo { Title = "ApexERP API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "JWT Authorization header using the Bearer scheme.",
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });
    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// ---- Apply pending EF Core migrations on startup. ----
// This requires migration files to exist under ApexERP.Infrastructure/Data/Migrations
// (generate them once with `dotnet ef migrations add InitialCreate ...` -- see
// scripts/generate-migrations.ps1 / .sh, and README "Database migrations"). Once committed,
// every environment (dev, CI, production) applies the exact same versioned schema on boot.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApexErpDbContext>();
    db.Database.Migrate();
}

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors("Frontend");
app.UseAuthentication();

// ---- Tenant resolution middleware: reads company_id from the validated JWT and sets it on the
// DbContext so every EF Core global query filter automatically scopes to the caller's company. ----
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true)
    {
        var companyIdClaim = context.User.FindFirst("company_id")?.Value;
        if (companyIdClaim != null && Guid.TryParse(companyIdClaim, out var companyId))
        {
            var dbContext = context.RequestServices.GetRequiredService<ApexErpDbContext>();
            dbContext.CurrentCompanyId = companyId;
        }
    }
    await next();
});

app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "ApexERP.API", timestamp = DateTime.UtcNow }));

app.Run();

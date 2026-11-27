using System.Security.Claims;
using ApexERP.API.Controllers;
using ApexERP.API.Services;
using ApexERP.Application.Services;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ApexERP.Tests;

public sealed class OrganisationHierarchyIntegrationTests
{
    private sealed class NoopEmailDeliveryService : IEmailDeliveryService
    {
        public Task<bool> SendAsync(string recipient, string subject, string body, CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    private static OrganisationManagementController Controller(ApexErpDbContext db, Guid companyId, Guid tenantId)
    {
        var claims = new[]
        {
            new Claim("company_id", companyId.ToString()),
            new Claim("tenant_id", tenantId.ToString()),
            new Claim("sub", Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "Administrator")
        };
        var controller = new OrganisationManagementController(db, new NoopEmailDeliveryService());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "integration-test")) }
        };
        return controller;
    }

    private static async Task<(Guid TenantId, Company Company)> SeedCompanyAsync(TestDb db)
    {
        await using var ctx = db.NewContext(null);
        var tenant = new CustomerOrganisation { Name = "Hierarchy tenant" };
        var company = new Company
        {
            TenantId = tenant.Id, Name = "Hierarchy company",
            BaseCurrency = "USD", FunctionalCurrency = "USD"
        };
        ctx.CustomerOrganisations.Add(tenant);
        ctx.Companies.Add(company);
        await ctx.SaveChangesAsync();
        return (tenant.Id, company);
    }

    [PostgresFact]
    public async Task Scheduled_jobs_execute_with_the_company_and_tenant_context_of_each_job()
    {
        await using var db = await TestDb.CreateAsync();
        var tenantA = new CustomerOrganisation { Name = "Scheduler tenant A" };
        var tenantB = new CustomerOrganisation { Name = "Scheduler tenant B" };
        var companyA = new Company { TenantId = tenantA.Id, Name = "Scheduler A", BaseCurrency = "USD", FunctionalCurrency = "USD" };
        var companyB = new Company { TenantId = tenantB.Id, Name = "Scheduler B", BaseCurrency = "USD", FunctionalCurrency = "USD" };
        await using (var seed = db.NewContext(null))
        {
            seed.CustomerOrganisations.AddRange(tenantA, tenantB);
            seed.Companies.AddRange(companyA, companyB);
            await seed.SaveChangesAsync();
        }

        var observed = new List<(Guid? CompanyId, Guid? TenantId)>();
        var services = new ServiceCollection();
        services.AddScoped<ApexErpDbContext>(_ => db.NewContext(null));
        using var provider = services.BuildServiceProvider();
        var job = new JobDefinition("tenant-context.probe", "Tenant context probe", _ => "same-run",
            (sp, _, _) =>
            {
                var context = sp.GetRequiredService<ApexErpDbContext>();
                observed.Add((context.CurrentCompanyId, context.CurrentTenantId));
                return Task.FromResult("context observed");
            });

        Assert.Equal(1, await ScheduledJobs.RunDueForCompanyAsync(provider, companyA.Id,
            new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc), new[] { job }));
        Assert.Equal(1, await ScheduledJobs.RunDueForCompanyAsync(provider, companyB.Id,
            new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc), new[] { job }));

        Assert.Equal(new (Guid? CompanyId, Guid? TenantId)[] { (companyA.Id, tenantA.Id), (companyB.Id, tenantB.Id) }, observed);
        await using var verify = db.NewContext(null);
        var executions = await verify.JobExecutions.IgnoreQueryFilters().Where(x => x.JobCode == job.Code).ToListAsync();
        Assert.Equal(2, executions.Count);
        Assert.All(executions, x => Assert.Equal("Succeeded", x.Status));
        Assert.Equal(new[] { companyA.Id, companyB.Id }.OrderBy(x => x),
            executions.Select(x => x.CompanyId).OrderBy(x => x));
    }

    [PostgresFact]
    public async Task Branch_updates_reject_cycles_and_deactivation_respects_active_children()
    {
        await using var db = await TestDb.CreateAsync();
        var (tenantId, company) = await SeedCompanyAsync(db);
        var root = new OrganisationalBranch { CompanyId = company.Id, Code = "ROOT", Name = "Root" };
        var child = new OrganisationalBranch { CompanyId = company.Id, Code = "CHILD", Name = "Child", ParentBranchId = root.Id };
        await using (var seed = db.NewContext(company.Id))
        {
            seed.OrganisationalBranches.AddRange(root, child);
            await seed.SaveChangesAsync();
        }

        await using var ctx = db.NewContext(company.Id);
        var api = Controller(ctx, company.Id, tenantId);
        var cyclic = await api.UpdateBranch(company.Id, root.Id,
            new UpdateBranchRequest("ROOT", "Root", child.Id), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(cyclic);

        var updated = await api.UpdateBranch(company.Id, child.Id,
            new UpdateBranchRequest("CHILD-2", "Child updated", root.Id), CancellationToken.None);
        Assert.IsType<OkObjectResult>(updated);

        var blocked = await api.DeactivateBranch(company.Id, root.Id, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(blocked);

        var deactivatedChild = await api.DeactivateBranch(company.Id, child.Id, CancellationToken.None);
        Assert.IsType<OkObjectResult>(deactivatedChild);
    }

    [PostgresFact]
    public async Task Department_and_cost_centre_updates_validate_company_branch_and_live_dependencies()
    {
        await using var db = await TestDb.CreateAsync();
        var (tenantId, company) = await SeedCompanyAsync(db);
        var otherCompanyId = Guid.NewGuid();
        var branch = new OrganisationalBranch { CompanyId = company.Id, Code = "HQ", Name = "Head office" };
        var foreignBranch = new OrganisationalBranch { CompanyId = otherCompanyId, Code = "EXT", Name = "Foreign branch" };
        var centre = new CostCentre { CompanyId = company.Id, Code = "OPS", Name = "Operations", BranchId = branch.Id };
        var department = new Department
        {
            CompanyId = company.Id, Name = "Operations", BranchId = branch.Id,
            CostCentreId = centre.Id, IsActive = true
        };
        var employee = new Employee
        {
            CompanyId = company.Id, EmployeeNo = "E-001", FirstName = "Test", LastName = "Employee",
            StartDate = DateTime.UtcNow.Date, BasicSalary = 100m, DepartmentId = department.Id, IsActive = true
        };
        await using (var seed = db.NewContext(company.Id))
        {
            seed.OrganisationalBranches.Add(branch);
            seed.CostCentres.Add(centre);
            seed.Departments.Add(department);
            seed.Employees.Add(employee);
            await seed.SaveChangesAsync();
        }
        await using (var foreignSeed = db.NewContext(otherCompanyId))
        {
            foreignSeed.Companies.Add(new Company { Id = otherCompanyId, Name = "Other company", BaseCurrency = "USD", FunctionalCurrency = "USD" });
            foreignSeed.OrganisationalBranches.Add(foreignBranch);
            await foreignSeed.SaveChangesAsync();
        }

        await using var ctx = db.NewContext(company.Id);
        var api = Controller(ctx, company.Id, tenantId);

        var crossCompanyBranch = await api.UpdateDepartment(company.Id, department.Id,
            new UpdateDepartmentRequest("Operations", foreignBranch.Id, centre.Id), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(crossCompanyBranch);

        var activeEmployeeBlock = await api.DeactivateDepartment(company.Id, department.Id, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(activeEmployeeBlock);

        var selfParent = await api.UpdateCostCentre(company.Id, centre.Id,
            new UpdateCostCentreRequest("OPS", "Operations", centre.Id, branch.Id), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(selfParent);

        var departmentUpdate = await api.UpdateDepartment(company.Id, department.Id,
            new UpdateDepartmentRequest("Operations & Support", branch.Id, centre.Id), CancellationToken.None);
        Assert.IsType<OkObjectResult>(departmentUpdate);

        var costCentreBlock = await api.DeactivateCostCentre(company.Id, centre.Id, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(costCentreBlock);

        var employeeRow = await ctx.Employees.SingleAsync(x => x.Id == employee.Id);
        employeeRow.IsActive = false;
        await ctx.SaveChangesAsync();
        var departmentDeactivated = await api.DeactivateDepartment(company.Id, department.Id, CancellationToken.None);
        Assert.IsType<OkObjectResult>(departmentDeactivated);

        var costCentreDeactivated = await api.DeactivateCostCentre(company.Id, centre.Id, CancellationToken.None);
        Assert.IsType<OkObjectResult>(costCentreDeactivated);
    }
}

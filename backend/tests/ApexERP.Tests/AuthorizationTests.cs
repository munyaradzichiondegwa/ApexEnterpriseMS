using System.Reflection;
using System.Security.Claims;
using ApexERP.API.Authorization;
using ApexERP.API.Controllers;
using ApexERP.API.Extensions;
using ApexERP.API.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ApexERP.Tests
{
    public class PermissionHandlerTests
    {
        private static async Task<bool> Evaluate(string required, params string[] permissionClaimValues)
        {
            var identity = new ClaimsIdentity(permissionClaimValues.Select(v => new Claim("permissions", v)), "test");
            var user = new ClaimsPrincipal(identity);
            var requirement = new PermissionRequirement(required);
            var context = new AuthorizationHandlerContext(new[] { requirement }, user, null);
            await new PermissionAuthorizationHandler().HandleAsync(context);
            return context.HasSucceeded;
        }

        [Fact] public async Task Exact_permission_succeeds() => Assert.True(await Evaluate("gl.journals.post", "[\"gl.journals.post\"]"));
        [Fact] public async Task Other_permission_fails() => Assert.False(await Evaluate("gl.journals.post", "[\"gl.journals.approve\"]"));
        [Fact] public async Task Star_succeeds() => Assert.True(await Evaluate("gl.journals.post", "[\"*\"]"));
        [Fact] public async Task Branch_wildcard_succeeds() => Assert.True(await Evaluate("payroll.runs.post", "[\"payroll.*\"]"));
        [Fact] public async Task No_claim_fails() => Assert.False(await Evaluate("gl.journals.post"));
        [Fact] public async Task Malformed_claim_fails() => Assert.False(await Evaluate("gl.journals.post", "[oops"));
        [Fact] public async Task A_read_only_role_cannot_post_reverse_or_close()
        {
            var readOnly = "[\"gl.accounts.read\",\"gl.journals.read\",\"reports.financial.read\"]";
            Assert.False(await Evaluate("gl.journals.post", readOnly));
            Assert.False(await Evaluate("gl.journals.reverse", readOnly));
            Assert.False(await Evaluate("gl.journals.approve", readOnly));
            Assert.False(await Evaluate("accounting.period.close", readOnly));
            Assert.True(await Evaluate("reports.financial.read", readOnly));
        }
    }

    public class PolicyProviderTests
    {
        private static PermissionPolicyProvider Provider() => new(Options.Create(new AuthorizationOptions()));

        [Fact]
        public async Task A_perm_policy_requires_an_authenticated_user_and_the_permission()
        {
            var policy = await Provider().GetPolicyAsync("perm:gl.journals.post");
            Assert.NotNull(policy);
            Assert.Contains(policy!.Requirements, r => r is PermissionRequirement p && p.Permission == "gl.journals.post");
            Assert.Contains(policy.Requirements, r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
        }

        [Fact]
        public async Task Unknown_policy_names_fall_back_to_the_default_provider()
        {
            Assert.Null(await Provider().GetPolicyAsync("no-such-policy"));
        }

        [Fact]
        public void The_attribute_builds_the_matching_policy_name()
        {
            Assert.Equal("perm:gl.journals.post", new HasPermissionAttribute("gl.journals.post").Policy);
        }
    }

    public class ConventionTests
    {
        [Theory]
        [InlineData("api/v1/gl", "gl")]
        [InlineData("api/v1/fixed-assets", "fixed-assets")]
        [InlineData("api/v1/Public-Sector", "public-sector")]
        [InlineData("api/v2/reports", "reports")]
        [InlineData("api/v1/payroll/", "payroll")]
        [InlineData("gl", "gl")]
        [InlineData("api/[controller]", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void Module_is_taken_from_the_route(string? template, string? expected)
        {
            Assert.Equal(expected, ModulePermissionConvention.ModuleFromTemplate(template));
        }

        [Route("api/v1/payroll")]
        private class FakePayrollController : ControllerBase
        {
            [HttpGet] public IActionResult List() => Ok();
            [HttpPost] public IActionResult Create() => Ok();
            [HttpPost("post")] [HasPermission("payroll.runs.post")] public IActionResult Post() => Ok();
            [HttpGet("mine")] [AuthenticatedOnly] public IActionResult Mine() => Ok();
            [AllowAnonymous] [HttpGet("public")] public IActionResult Public() => Ok();
            [HttpGet("a"), HttpPost("a")] public IActionResult Mixed() => Ok();
        }

        private static ControllerModel Model(Type type)
        {
            var controller = new ControllerModel(type.GetTypeInfo(), type.GetCustomAttributes(true).Cast<object>().ToList());
            controller.Selectors.Add(new SelectorModel
            {
                AttributeRouteModel = new AttributeRouteModel { Template = type.GetCustomAttribute<RouteAttribute>()?.Template }
            });

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var attributes = method.GetCustomAttributes(true).Cast<object>().ToList();
                var action = new ActionModel(method, attributes) { ActionName = method.Name, Controller = controller };
                var selector = new SelectorModel();
                var verbs = attributes.OfType<IActionHttpMethodProvider>().SelectMany(a => a.HttpMethods).ToArray();
                if (verbs.Length > 0) selector.ActionConstraints.Add(new HttpMethodActionConstraint(verbs));
                action.Selectors.Add(selector);
                controller.Actions.Add(action);
            }

            var application = new ApplicationModel();
            application.Controllers.Add(controller);
            controller.Application = application;
            new ModulePermissionConvention().Apply(application);
            return controller;
        }

        private static string[] Required(ActionModel action) =>
            action.Filters.OfType<AuthorizeFilter>()
                .SelectMany(f => f.AuthorizeData ?? Array.Empty<IAuthorizeData>())
                .Select(d => d.Policy!)
                .ToArray();

        [Fact]
        public void Reads_default_to_module_read_and_writes_to_module_write()
        {
            var controller = Model(typeof(FakePayrollController));
            Assert.Equal(new[] { "perm:payroll.read" }, Required(controller.Actions.Single(a => a.ActionName == "List")));
            Assert.Equal(new[] { "perm:payroll.write" }, Required(controller.Actions.Single(a => a.ActionName == "Create")));
        }

        [Fact]
        public void An_action_mixing_reads_and_writes_needs_the_stricter_write_permission()
        {
            var controller = Model(typeof(FakePayrollController));
            Assert.Equal(new[] { "perm:payroll.write" }, Required(controller.Actions.Single(a => a.ActionName == "Mixed")));
        }

        [Fact]
        public void An_explicit_permission_replaces_the_module_default()
        {
            var controller = Model(typeof(FakePayrollController));
            Assert.Empty(Required(controller.Actions.Single(a => a.ActionName == "Post")));   // the attribute itself is evaluated by the framework
            Assert.Contains(controller.Actions.Single(a => a.ActionName == "Post").Attributes, a => a is HasPermissionAttribute);
        }

        [Fact]
        public void Authenticated_only_and_anonymous_actions_get_no_extra_requirement()
        {
            var controller = Model(typeof(FakePayrollController));
            Assert.Empty(Required(controller.Actions.Single(a => a.ActionName == "Mine")));
            Assert.Empty(Required(controller.Actions.Single(a => a.ActionName == "Public")));
        }
    }

    public class TransactionPolicyTests
    {
        [Theory]
        [InlineData("GET", false)]
        [InlineData("HEAD", false)]
        [InlineData("OPTIONS", false)]
        [InlineData("TRACE", false)]
        [InlineData("POST", true)]
        [InlineData("PUT", true)]
        [InlineData("PATCH", true)]
        [InlineData("DELETE", true)]
        public void Only_mutating_requests_get_a_transaction(string method, bool expected) =>
            Assert.Equal(expected, TransactionPolicy.IsMutating(method));

        [Theory]
        [InlineData(200, false, true)]
        [InlineData(201, false, true)]
        [InlineData(204, false, true)]
        [InlineData(null, false, true)]     // a result without a status code (e.g. a file) is a success
        [InlineData(302, false, true)]
        [InlineData(400, false, false)]     // business-rule failure => roll back partial writes
        [InlineData(401, false, false)]
        [InlineData(403, false, false)]
        [InlineData(404, false, false)]
        [InlineData(409, false, false)]
        [InlineData(500, false, false)]
        [InlineData(200, true, false)]      // an unhandled exception always rolls back
        [InlineData(null, true, false)]
        public void Commit_decision(int? status, bool unhandledException, bool expected) =>
            Assert.Equal(expected, TransactionPolicy.ShouldCommit(status, unhandledException));
    }

    /// <summary>
    /// Guard against the original finding 2: builds the REAL MVC endpoint descriptors (through the same AddApexControllers()
    /// call that Program.cs uses) for EVERY controller in the API, and asserts that no action can be reached by merely being
    /// signed in. A new controller or action that forgets authorization fails here.
    /// </summary>
    public class ControllerAuthorizationGuardTests
    {
        private static IReadOnlyList<ControllerActionDescriptor> Descriptors()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Services.AddApexControllers().AddApplicationPart(typeof(ApiControllerBase).Assembly);
            using var app = builder.Build();
            return app.Services.GetRequiredService<IActionDescriptorCollectionProvider>()
                .ActionDescriptors.Items.OfType<ControllerActionDescriptor>().ToList();
        }

        /// <summary>
        /// Every authorization requirement on an action. With endpoint routing, [Authorize]/[HasPermission] attributes are endpoint
        /// metadata (enforced by the authorization middleware), while the convention's default module permission is an MVC filter.
        /// </summary>
        private static string[] Policies(ControllerActionDescriptor d) =>
            d.FilterDescriptors.Select(f => f.Filter).OfType<AuthorizeFilter>()
                .SelectMany(f => f.AuthorizeData ?? Array.Empty<IAuthorizeData>())
                .Concat(d.EndpointMetadata.OfType<IAuthorizeData>())
                .Select(a => a.Policy ?? "(authenticated)")
                .ToArray();

        private static bool Anonymous(ControllerActionDescriptor d) => d.EndpointMetadata.OfType<IAllowAnonymous>().Any();

        [Fact]
        public void The_guard_actually_sees_the_controllers()
        {
            var all = Descriptors();
            Assert.True(all.Count > 100, $"Expected the full API surface, found {all.Count} actions.");
            Assert.Contains(all, d => d.ControllerTypeInfo.AsType() == typeof(GlController));
        }

        [Fact]
        public void Every_action_requires_authentication_or_is_an_explicit_auth_endpoint()
        {
            var open = Descriptors()
                .Where(d => !Policies(d).Any())                                   // not behind ANY authorize filter
                .Where(d => !(Anonymous(d) && d.ControllerTypeInfo.AsType() == typeof(AuthController)))
                .Select(d => $"{d.ControllerName}.{d.ActionName}")
                .ToList();
            Assert.True(open.Count == 0, "Actions reachable without signing in: " + string.Join(", ", open));
        }

        [Fact]
        public void Anonymous_access_exists_only_on_the_auth_controller()
        {
            var anonymous = Descriptors().Where(Anonymous).Select(d => d.ControllerTypeInfo.AsType()).Distinct().ToList();
            Assert.Equal(new[] { typeof(AuthController) }, anonymous);
        }

        [Fact]
        public void Every_non_auth_action_requires_a_permission_not_just_a_login()
        {
            var onlyLogin = Descriptors()
                .Where(d => d.ControllerTypeInfo.AsType() != typeof(AuthController))
                .Where(d => !d.EndpointMetadata.OfType<AuthenticatedOnlyAttribute>().Any())
                .Where(d => !Policies(d).Any(p => p.StartsWith(HasPermissionAttribute.PolicyPrefix, StringComparison.Ordinal)))
                .Select(d => $"{d.ControllerName}.{d.ActionName}")
                .ToList();
            Assert.True(onlyLogin.Count == 0, "Actions open to any signed-in user: " + string.Join(", ", onlyLogin));
        }

        [Fact]
        public void The_only_authenticated_only_actions_are_the_intended_self_service_ones()
        {
            var selfService = Descriptors()
                .Where(d => d.EndpointMetadata.OfType<AuthenticatedOnlyAttribute>().Any())
                .Select(d => $"{d.ControllerName}.{d.ActionName}")
                .OrderBy(n => n)
                .ToList();
            Assert.Equal(new[] { "Workflow.GetPending" }, selfService);
        }

        private static string Permission(ControllerActionDescriptor d) =>
            d.EndpointMetadata.OfType<HasPermissionAttribute>().Single().Permission;

        private static ControllerActionDescriptor Find(Type controller, string action) =>
            Descriptors().Single(d => d.ControllerTypeInfo.AsType() == controller && d.ActionName == action);

        [Fact]
        public void The_sensitive_gl_actions_name_their_own_permission()
        {
            Assert.Equal("gl.journals.create", Permission(Find(typeof(GlController), nameof(GlController.CreateJournalEntry))));
            Assert.Equal("gl.journals.approve", Permission(Find(typeof(GlController), nameof(GlController.ApproveJournalEntry))));
            Assert.Equal("gl.journals.post", Permission(Find(typeof(GlController), nameof(GlController.PostJournalEntry))));
            Assert.Equal("gl.journals.reverse", Permission(Find(typeof(GlController), nameof(GlController.ReverseJournalEntry))));
            Assert.Equal("accounting.period.close", Permission(Find(typeof(GlController), nameof(GlController.ClosePeriod))));
            Assert.Equal("accounting.period.reopen", Permission(Find(typeof(GlController), nameof(GlController.ReopenPeriod))));
            Assert.Equal("gl.accounts.manage", Permission(Find(typeof(GlController), nameof(GlController.CreateAccount))));
        }

        [Fact]
        public void Administration_endpoints_are_protected_by_admin_permissions()
        {
            var admin = Descriptors().Where(d => d.ControllerName == "Admin").ToList();
            Assert.Equal("admin.roles.manage", Permission(admin.Single(d => d.ActionName == "CreateRole")));
            Assert.Equal("admin.users.manage", Permission(admin.Single(d => d.ActionName == "CreateUser")));
            Assert.Equal("admin.company.manage", Permission(admin.Single(d => d.ActionName == "UpdateCompany")));
        }

        [Fact]
        public void Group_structure_netting_and_consolidation_are_protected()
        {
            Assert.Equal("group.structure.manage", Permission(Find(typeof(GroupController), nameof(GroupController.UpdateGroupStructure))));
            Assert.Equal("group.netting.execute", Permission(Find(typeof(GroupController), nameof(GroupController.ExecuteNetting))));
            Assert.Equal("group.consolidation.read", Permission(Find(typeof(GroupController), nameof(GroupController.GetConsolidatedStatements))));
        }

        [Fact]
        public void Posting_and_reporting_actions_in_other_modules_are_protected()
        {
            Assert.Equal("ar.invoices.post", Permission(Find(typeof(ArController), nameof(ArController.PostInvoice))));
            Assert.Equal("reports.financial.read", Permission(Find(typeof(ReportingController), nameof(ReportingController.GetBalanceSheet))));
            Assert.Equal("reports.financial.read", Permission(Find(typeof(ReportingController), nameof(ReportingController.GetTrialBalance))));
        }

        [Fact]
        public void Mutating_requests_run_inside_the_request_transaction_filter()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Services.AddApexControllers().AddApplicationPart(typeof(ApiControllerBase).Assembly);
            using var app = builder.Build();
            var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<MvcOptions>>().Value;
            Assert.Contains(options.Filters, f => f is TypeFilterAttribute t && t.ImplementationType == typeof(TransactionalActionFilter));
        }
    }
}

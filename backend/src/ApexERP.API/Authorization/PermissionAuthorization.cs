using ApexERP.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace ApexERP.API.Authorization
{
    public sealed class PermissionRequirement : IAuthorizationRequirement
    {
        public PermissionRequirement(string permission) => Permission = permission;
        public string Permission { get; }
    }

    /// <summary>Evaluates a <see cref="PermissionRequirement"/> against the "permissions" claim(s) of the caller.</summary>
    public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
    {
        public const string ClaimType = "permissions";

        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
        {
            var granted = PermissionEvaluator.Parse(context.User.FindAll(ClaimType).Select(c => c.Value));
            if (PermissionEvaluator.Has(granted, requirement.Permission))
                context.Succeed(requirement);

            // Not calling Fail(): an unmet requirement already results in 403 for an authenticated caller.
            return Task.CompletedTask;
        }
    }

    /// <summary>Builds "perm:xxx" policies on demand so no policy has to be registered one by one.</summary>
    public sealed class PermissionPolicyProvider : IAuthorizationPolicyProvider
    {
        private readonly DefaultAuthorizationPolicyProvider _fallback;

        public PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
        {
            _fallback = new DefaultAuthorizationPolicyProvider(options);
        }

        public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

        public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

        public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
        {
            if (policyName.StartsWith(HasPermissionAttribute.PolicyPrefix, StringComparison.Ordinal))
            {
                var permission = policyName.Substring(HasPermissionAttribute.PolicyPrefix.Length);
                var policy = new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .AddRequirements(new PermissionRequirement(permission))
                    .Build();
                return Task.FromResult<AuthorizationPolicy?>(policy);
            }

            return _fallback.GetPolicyAsync(policyName);
        }
    }
}

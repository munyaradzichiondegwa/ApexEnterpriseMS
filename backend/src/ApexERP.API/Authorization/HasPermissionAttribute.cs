using Microsoft.AspNetCore.Authorization;

namespace ApexERP.API.Authorization
{
    /// <summary>
    /// Requires the signed-in user's role to grant <paramref name="permission"/> (see
    /// <c>ApexERP.Application.Security.Permissions</c>). The check is evaluated by the API on every
    /// request; the "permissions" claim in the token is only evidence, never a substitute for this check.
    /// Several attributes on one action must ALL be satisfied.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
    public sealed class HasPermissionAttribute : AuthorizeAttribute
    {
        public const string PolicyPrefix = "perm:";

        public HasPermissionAttribute(string permission) : base(PolicyPrefix + permission)
        {
            Permission = permission;
        }

        public string Permission { get; }
    }

    /// <summary>
    /// Marks an action as intentionally available to every signed-in user of the tenant (for example
    /// "my pending approvals"). Without this marker or an explicit <see cref="HasPermissionAttribute"/>,
    /// <c>ModulePermissionConvention</c> applies the module's read/write permission.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class AuthenticatedOnlyAttribute : Attribute
    {
    }
}

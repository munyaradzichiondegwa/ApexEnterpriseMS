using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Authorization;

namespace ApexERP.API.Authorization
{
    /// <summary>
    /// Deny-by-default RBAC. Every controller action that does not carry an explicit
    /// <see cref="HasPermissionAttribute"/> (or <see cref="AuthenticatedOnlyAttribute"/> /
    /// [AllowAnonymous]) automatically requires <c>{module}.read</c> (GET/HEAD/OPTIONS) or
    /// <c>{module}.write</c> (everything else), where {module} is the route segment after "api/v1/"
    /// (e.g. api/v1/payroll => payroll.read / payroll.write). This closes the gap where any signed-in
    /// user could reach every endpoint (audit finding 2) without annotating ~140 actions by hand,
    /// and guarantees a new controller is never accidentally left open.
    /// </summary>
    public sealed class ModulePermissionConvention : IApplicationModelConvention
    {
        private static readonly HashSet<string> ReadOnlyVerbs =
            new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS" };

        public void Apply(ApplicationModel application)
        {
            foreach (var controller in application.Controllers)
            {
                var module = ModuleOf(controller);
                if (module == null || module == "auth") continue;

                foreach (var action in controller.Actions)
                {
                    if (IsExplicit(controller, action)) continue;

                    var permission = $"{module}.{(IsReadOnly(action) ? "read" : "write")}";
                    action.Filters.Add(new AuthorizeFilter(new IAuthorizeData[] { new HasPermissionAttribute(permission) }));
                }
            }
        }

        /// <summary>"api/v1/fixed-assets" => "fixed-assets". Returns null when the route has no module segment.</summary>
        public static string? ModuleOf(ControllerModel controller)
        {
            var template = controller.Selectors
                .Select(s => s.AttributeRouteModel?.Template)
                .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            return ModuleFromTemplate(template);
        }

        public static string? ModuleFromTemplate(string? template)
        {
            if (string.IsNullOrWhiteSpace(template)) return null;
            var segments = template.Split('/', StringSplitOptions.RemoveEmptyEntries);

            // "api/v1/gl" -> segment after the version. Without a version, skip a leading "api" ("api/gl" -> gl).
            var versionIndex = Array.FindIndex(segments, s => s.Length >= 2 && s[0] == 'v' && s.Skip(1).All(char.IsDigit));
            int moduleIndex;
            if (versionIndex >= 0) moduleIndex = versionIndex + 1;
            else if (segments.Length > 0 && segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)) moduleIndex = 1;
            else moduleIndex = 0;

            if (moduleIndex >= segments.Length) return null;
            var module = segments[moduleIndex].ToLowerInvariant();
            return module.StartsWith("[", StringComparison.Ordinal) ? null : module;   // "[controller]" is not a fixed module
        }

        private static bool IsExplicit(ControllerModel controller, ActionModel action) =>
            action.Attributes.OfType<IAllowAnonymous>().Any() ||
            controller.Attributes.OfType<IAllowAnonymous>().Any() ||
            action.Attributes.OfType<HasPermissionAttribute>().Any() ||
            controller.Attributes.OfType<HasPermissionAttribute>().Any() ||
            action.Attributes.OfType<AuthenticatedOnlyAttribute>().Any() ||
            controller.Attributes.OfType<AuthenticatedOnlyAttribute>().Any();

        private static bool IsReadOnly(ActionModel action)
        {
            var verbs = action.Selectors
                .SelectMany(s => s.ActionConstraints)
                .OfType<HttpMethodActionConstraint>()
                .SelectMany(c => c.HttpMethods)
                .ToList();

            // No explicit verb => treat as a write (the stricter permission).
            return verbs.Count > 0 && verbs.All(v => ReadOnlyVerbs.Contains(v));
        }
    }
}

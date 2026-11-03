using ApexERP.API.Authorization;
using ApexERP.API.Filters;

namespace ApexERP.API.Extensions
{
    public static class MvcSetup
    {
        /// <summary>
        /// Registers the controllers with the cross-cutting security behaviour. Program.cs and the authorization guard tests
        /// both call this, so the tests inspect exactly what production runs.
        /// </summary>
        public static IMvcBuilder AddApexControllers(this IServiceCollection services) =>
            services.AddControllers(o =>
            {
                // Deny by default: every action needs {module}.read / {module}.write unless it names its own permission.
                o.Conventions.Add(new ModulePermissionConvention());
                // One database transaction per mutating request, so multi-step postings are all-or-nothing.
                o.Filters.Add<TransactionalActionFilter>();
            }).AddJsonOptions(o =>
            {
                o.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
                o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            });
    }
}

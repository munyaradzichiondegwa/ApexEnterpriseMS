using System;

namespace ApexERP.API.Authorization;

/// <summary>
/// Marks a controller whose access is governed by a dedicated control-plane policy rather than the
/// ordinary tenant module permission convention. The controller must still declare an explicit
/// [Authorize] policy that enforces the separate platform identity boundary.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class ModulePermissionExemptAttribute : Attribute { }

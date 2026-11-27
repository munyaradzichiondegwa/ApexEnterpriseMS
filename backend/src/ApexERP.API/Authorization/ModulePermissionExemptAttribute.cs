using System;

namespace ApexERP.API.Authorization;

/// <summary>
/// Marks a controller whose access is governed by a dedicated control-plane or tenant-administration
/// policy rather than the ordinary module route permission convention. The controller must still
/// declare an explicit [Authorize] policy or authenticated user boundary.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class ModulePermissionExemptAttribute : Attribute { }

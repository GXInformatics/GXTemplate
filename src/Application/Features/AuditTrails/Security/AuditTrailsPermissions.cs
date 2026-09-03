// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.Security;

public static partial class Permissions
{
    [DisplayName("Audit Permissions")]
    [Description("Set permissions for audit operations")]
    public static class AuditTrails
    {
        [Description("Allows viewing audit trail details")]
        public const string View = "Permissions.AuditTrails.View";

        [Description("Allows searching for audit trail records")]
        public const string Search = "Permissions.AuditTrails.Search";

        [Description("Allows exporting audit trail records")]
        public const string Export = "Permissions.AuditTrails.Export";

        /// <summary>
        /// Sees audit rows from every tenant, rather than only the one this principal is acting in.
        /// </summary>
        /// <remarks>
        /// <b>The escape from the global query filter.</b> Pass 29 gave <c>AuditTrail</c> a named
        /// tenant filter on <c>ApplicationDbContext</c>, so every read is scoped by default and a
        /// new query is scoped whether or not its author thought about it. This right is what lets
        /// a caller drop that filter - and only that filter, by name.
        /// <para>
        /// <b>Checked at the call site, not inside the filter.</b> It cannot be a term in the
        /// predicate: <c>UserContext</c> carries the tenant, the allowed tenants and the roles, but
        /// no permissions, and a query filter expression cannot perform the permission query it
        /// would need. So the shape is Pass 27's - resolve the right once, then pass an explicit
        /// <c>IgnoreQueryFilters(["Tenant"])</c> carrying a stated reason.
        /// </para>
        /// <para>
        /// <b>Deliberately its own right, not <c>Users.ViewAllTenants</c>.</b> Seeing every tenant's
        /// USERS and seeing every tenant's AUDIT HISTORY are different disclosures: the second
        /// includes the before-and-after values of every audited change. An administrator who
        /// manages accounts across tenants does not thereby need to read what those tenants did.
        /// </para>
        /// </remarks>
        [Description("Allows viewing audit trails from every tenant, not only the principal's own")]
        public const string ViewAllTenants = "Permissions.AuditTrails.ViewAllTenants";
    }
}

public class AuditTrailsAccessRights
{
    public bool View { get; set; }
    public bool Search { get; set; }
    public bool Export { get; set; }

    // The property NAME is what PermissionService turns into the claim string, so this must stay
    // spelled exactly like the constant above - see LogsAccessRights for what a mismatch costs.
    public bool ViewAllTenants { get; set; }
} 

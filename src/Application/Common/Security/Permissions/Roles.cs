// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.Security;

public static partial class Permissions
{
    [DisplayName("Role Permissions")]
    [Description("Set permissions for role operations")]
    public static class Roles
    {
        [Description("Allows viewing role details")]
        public const string View = "Permissions.Roles.View";

        [Description("Allows creating new roles")]
        public const string Create = "Permissions.Roles.Create";

        [Description("Allows modifying existing roles")]
        public const string Edit = "Permissions.Roles.Edit";

        [Description("Allows deleting roles")]
        public const string Delete = "Permissions.Roles.Delete";

        [Description("Allows searching for role records")]
        public const string Search = "Permissions.Roles.Search";

        [Description("Allows importing role data")]
        public const string Import = "Permissions.Roles.Import";
        [Description("Allows exporting role data")]
        public const string Export= "Permissions.Roles.Export";

        [Description("Allows managing role permissions")]
        public const string ManagePermissions = "Permissions.Roles.ManagePermissions";

        [Description("Allows managing role claims")]
        public const string ManageClaimsInRole = "Permissions.Roles.ManageClaimsInRole";

        [Description("Allows managing users in role")]
        public const string ManageUsersInRole = "Permissions.Roles.ManageUsersInRole";

        [Description("Allows viewing role permissions")]
        public const string ViewPermissions = "Permissions.Roles.ViewPermissions";

        [Description("Allows viewing role claims")]
        public const string ViewClaimsInRole = "Permissions.Roles.ViewClaimsInRole";

        [Description("Allows viewing users in role")]
        public const string ViewUsersInRole = "Permissions.Roles.ViewUsersInRole";

        /// <summary>
        /// Who may DEFINE a role - create it, rename it, delete it, re-permission it, or import one.
        /// Assigning a user to an EXISTING role is not this right; that is an operation on the user
        /// and stays on <c>Permissions.Users.*</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Roles are installation-wide.</b> <c>ApplicationRole</c> carries no tenant, and
        /// Identity's own <c>RoleNameIndex</c> is unique across the whole installation, so every
        /// tenant's users sit in the same role rows. Before this right existed a tenant
        /// administrator holding <c>Roles.Edit</c> could rename a role another tenant relies on,
        /// <c>Roles.Delete</c> could remove it, and <c>Roles.ManagePermissions</c> could revoke a
        /// capability from every ordinary user in every tenant at once. Nothing prevented any of
        /// the three - it was the strongest cross-tenant WRITE left in the template.
        /// </para>
        /// <para>
        /// <b>Granted to the administrator by default</b>, for the reason
        /// <c>PicklistSets.ManageShared</c> is: the single-tenant deployment is the common case and
        /// its sole administrator must manage roles out of the box. Revoking it is the multi-tenant
        /// operator's deliberate act. The trap this avoids is a blanket prohibition, not a
        /// default-granted right.
        /// </para>
        /// <para>
        /// <b>One right rather than one per verb.</b> The section's other constants are per-verb,
        /// but this is not a verb - it is the boundary between administering the installation's
        /// roles and administering your own tenant's users. Splitting it would invite a grant that
        /// lets someone delete a role but not fix it, which is worse than either.
        /// </para>
        /// </remarks>
        [Description("Allows defining roles - creating, renaming, deleting, re-permissioning and importing them")]
        public const string ManageDefinitions = "Permissions.Roles.ManageDefinitions";
    }
}

public class RolesAccessRights
{
    public bool View { get; set; }
    public bool Create { get; set; }
    public bool Edit { get; set; }
    public bool Delete { get; set; }
    public bool Search { get; set; }
    public bool Export { get; set; }
    public bool Import { get; set; }
    public bool ManagePermissions { get; set; }
    public bool ManageClaimsInRole { get; set; }
    public bool ManageUsersInRole { get; set; }
    public bool ViewPermissions { get; set; }
    public bool ViewClaimsInRole { get; set; }
    public bool ViewUsersInRole { get; set; }

    // The property NAME is what PermissionService turns into the claim string, so this must stay
    // spelled exactly like the constant above - see LogsAccessRights for what a mismatch costs.
    public bool ManageDefinitions { get; set; }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.Security;

public static partial class Permissions
{
    /// <summary>
    /// The role rights. <b>Every one of them acts on installation-wide data.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the descriptions say so.</b> <c>[Description]</c> text is what the role editor renders
    /// under a permission's name at the moment an administrator decides to grant it. Every other
    /// permission group in this template describes a tenant-scoped capability, so a reader
    /// reasonably assumes the product's default - and roles are the exception:
    /// <c>ApplicationRole</c> carries no tenant and Identity's own <c>RoleNameIndex</c> is unique
    /// across the installation, so every tenant's users sit in the same role rows. Pass 35 §3.3
    /// flagged this whole group as reading tenant-scoped when it is not; Pass 40 corrected it.
    /// </para>
    /// <para>
    /// <b>Definition versus assignment, which is the distinction that matters when granting.</b>
    /// <see cref="Create"/>, <see cref="Edit"/>, <see cref="Delete"/>, <see cref="Import"/> and
    /// <see cref="ManagePermissions"/> DEFINE a role, and since Pass 33 each additionally requires
    /// <see cref="ManageDefinitions"/> - so their descriptions say "also needs Manage Definitions"
    /// rather than leaving an operator to discover the pairing by being refused. Assigning a user to
    /// an existing role is not here at all: it is an operation on the user, on
    /// <c>Permissions.Users.ManageRoles</c>.
    /// </para>
    /// <para>
    /// <b>Five constants name a surface this template does not have</b> - the claims-in-role and
    /// users-in-role administration, and the read-only permission viewer. They are EXCLUDED in
    /// <c>AdministratorPermissionRegistry</c>, but exclusion keeps them out of the administrator's
    /// grant, not out of the role editor's list, so their descriptions now say plainly that they do
    /// nothing. A permission that appears grantable and is inert costs an administrator a decision
    /// for no capability.
    /// </para>
    /// </remarks>
    [DisplayName("Role Permissions")]
    [Description("Set permissions for role operations - roles are installation-wide")]
    public static class Roles
    {
        [Description("Allows viewing the installation's roles - every tenant shares them")]
        public const string View = "Permissions.Roles.View";

        [Description("Allows creating a role, for every tenant - also needs Manage Definitions")]
        public const string Create = "Permissions.Roles.Create";

        [Description("Allows renaming a role every tenant shares - also needs Manage Definitions")]
        public const string Edit = "Permissions.Roles.Edit";

        [Description("Allows deleting a role every tenant shares - also needs Manage Definitions")]
        public const string Delete = "Permissions.Roles.Delete";

        [Description("Allows searching the installation's roles")]
        public const string Search = "Permissions.Roles.Search";

        [Description("Allows importing roles for the whole installation - also needs Manage Definitions")]
        public const string Import = "Permissions.Roles.Import";
        [Description("Allows exporting the installation's roles - reading only, not defining")]
        public const string Export= "Permissions.Roles.Export";

        [Description("Allows re-permissioning a role for every tenant at once - also needs Manage Definitions")]
        public const string ManagePermissions = "Permissions.Roles.ManagePermissions";

        [Description("Not implemented in this template - there is no claims-in-role administration")]
        public const string ManageClaimsInRole = "Permissions.Roles.ManageClaimsInRole";

        [Description("Not implemented in this template - assign users to roles from the Users page")]
        public const string ManageUsersInRole = "Permissions.Roles.ManageUsersInRole";

        [Description("Not implemented in this template - viewing happens inside the Set Permissions dialog")]
        public const string ViewPermissions = "Permissions.Roles.ViewPermissions";

        [Description("Not implemented in this template - there is no claims-in-role viewer")]
        public const string ViewClaimsInRole = "Permissions.Roles.ViewClaimsInRole";

        [Description("Not implemented in this template - see the Users page's role column")]
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
        [Description("Allows DEFINING the installation's roles - create, rename, delete, re-permission, import")]
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

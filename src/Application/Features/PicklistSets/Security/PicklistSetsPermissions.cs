// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.Security;

public static partial class Permissions
{
    [DisplayName("Picklist Permissions")]
    [Description("Set permissions for picklist operations")]
    public static class PicklistSets
    {
        [Description("Allows viewing picklist set details")]
        public const string View = "Permissions.PicklistSets.View";

        [Description("Allows creating new picklist sets")]
        public const string Create = "Permissions.PicklistSets.Create";

        [Description("Allows modifying existing picklist sets")]
        public const string Edit = "Permissions.PicklistSets.Edit";

        [Description("Allows deleting picklist sets")]
        public const string Delete = "Permissions.PicklistSets.Delete";

        [Description("Allows searching for picklist set records")]
        public const string Search = "Permissions.PicklistSets.Search";

        [Description("Allows exporting picklist set records")]
        public const string Export = "Permissions.PicklistSets.Export";

        [Description("Allows importing picklist set records")]
        public const string Import = "Permissions.PicklistSets.Import";

        /// <summary>
        /// Creates, modifies and deletes the SHARED picklist values every tenant sees.
        /// </summary>
        /// <remarks>
        /// <b>A write right over the shared partition, not a read escape.</b> Pass 31 made picklists
        /// shared reference data plus per-tenant additions: a row with no <c>TenantId</c> is
        /// installation-wide and visible to everyone, and the global query filter admits it for
        /// reading. Nothing hides a shared row from anybody, so this right widens no disclosure - it
        /// decides who may CHANGE a value every tenant depends on.
        /// <para>
        /// <b>It does not reopen Pass 31 §C.</b> That declined a cross-tenant READ escape - nobody
        /// needs to see another tenant's private picklists - and that still stands. This right grants
        /// nothing over another tenant's private rows: they remain invisible, and a query filter the
        /// holder cannot drop keeps them so. The two are orthogonal.
        /// </para>
        /// <para>
        /// <b>Why a right rather than a blanket read-only rule.</b> The bootstrap administrator is
        /// itself tenant-scoped - <c>EnsureAdministratorAsync</c> assigns it <c>Tenants.First()</c> -
        /// so "shared rows are read-only to a tenant-scoped principal" would freeze the seeded values
        /// for the life of the installation. It would bite hardest in the single-tenant deployment
        /// that is the common case, where the sole administrator would face reference data they
        /// cannot edit beside their own rows which they can. Granted to the administrator by default
        /// for exactly that reason, and revocable in a multi-tenant installation where a tenant
        /// administrator should not be able to change what every other tenant sees.
        /// </para>
        /// <para>
        /// <b>One right for create, edit and delete rather than three.</b> The section's other
        /// constants are per-verb, but this is not a verb - it is a partition. Splitting it would
        /// invite a grant that lets a principal delete a shared value but not fix it.
        /// </para>
        /// </remarks>
        [Description("Allows creating, editing and deleting the shared picklist values every tenant sees")]
        public const string ManageShared = "Permissions.PicklistSets.ManageShared";
    }
}

public class PicklistSetsAccessRights
{
    public bool View { get; set; }
    public bool Create { get; set; }
    public bool Edit { get; set; }
    public bool Delete { get; set; }
    public bool Search { get; set; }
    public bool Export { get; set; }
    public bool Import { get; set; }

    // The property NAME is what PermissionService turns into the claim string, so this must stay
    // spelled exactly like the constant above - see LogsAccessRights for what a mismatch costs.
    public bool ManageShared { get; set; }
}

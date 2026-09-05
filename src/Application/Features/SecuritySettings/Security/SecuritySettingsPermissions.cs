// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.Security;

public static partial class Permissions
{
    [DisplayName("Security Settings Permissions")]
    [Description("Set permissions for the installation's security policy")]
    public static class SecuritySettings
    {
        [Description("Allows viewing the security policy")]
        public const string View = "Permissions.SecuritySettings.View";

        // Deliberately its own permission rather than a general administration right: changing how
        // long a session may sit unattended is a security control, and the set of people who should
        // hold it is not the same as the set who administer users or picklists.
        //
        // It is NOT on its own sufficient to save: the policy is installation-wide, so the write
        // also requires ManageInstallationPolicy below. Edit remains the feature right - what the
        // screen is for - and the new one is the scope right.
        [Description("Allows changing the security policy, including the idle timeout")]
        public const string Edit = "Permissions.SecuritySettings.Edit";

        /// <summary>
        /// Who may write the security policy, which is INSTALLATION-WIDE: one row, in force for
        /// every tenant at once.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The policy is installation-wide deliberately, not pending a redesign.</b>
        /// <c>IdleTimeoutSettings.CookieLifetime</c> derives from <c>MaxIdleTimeoutMinutes</c>, and
        /// the authentication cookie is issued at sign-in - <b>before any tenant is known</b>, and it
        /// cannot be shortened afterwards. The outer bound is therefore irreducibly installation-wide,
        /// so per-tenant rows could only ever let a customer pick a point inside a band the operator
        /// has already fixed in configuration. See <c>IdleTimeoutPolicyProvider</c> for the condition
        /// under which that smaller thing would nonetheless be worth building.
        /// </para>
        /// <para>
        /// <b>Granted to the administrator by default</b>, for the reason
        /// <c>PicklistSets.ManageShared</c> and <c>Roles.ManageDefinitions</c> are:
        /// <c>EnsureAdministratorAsync</c> assigns the bootstrap administrator <c>Tenants.First()</c>,
        /// so the sole administrator of a single-tenant installation is ITSELF tenant-scoped. A right
        /// defaulting to ungranted - or any rule of the form "a tenant-scoped principal may not edit
        /// the installation policy" - would leave that installation permanently unable to change its
        /// own idle timeout: screen present, values shown, save refused. The trap is a blanket
        /// prohibition, not a default-granted right.
        /// </para>
        /// <para>
        /// <b>Reading is unaffected.</b> <see cref="View"/> still shows the policy to anyone who could
        /// see it before. Only the write narrows - which is the whole point of introducing a second
        /// right rather than re-scoping the first.
        /// </para>
        /// </remarks>
        [Description("Allows changing the session policy for EVERY tenant - it is installation-wide")]
        public const string ManageInstallationPolicy = "Permissions.SecuritySettings.ManageInstallationPolicy";
    }
}

public class SecuritySettingsAccessRights
{
    public bool View { get; set; }
    public bool Edit { get; set; }

    // The property NAME is what PermissionService turns into the claim string, so this must stay
    // spelled exactly like the constant above - see LogsAccessRights for what a mismatch costs.
    public bool ManageInstallationPolicy { get; set; }
}

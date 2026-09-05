// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Application.Common.ExceptionHandlers;
using CleanArchitecture.Blazor.Application.Common.Security;

namespace CleanArchitecture.Blazor.Application.Features.Identity;

/// <summary>
/// Who may DEFINE a role: create it, rename it, delete it, re-permission it, or import one.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is NOT.</b> Assigning a user to an existing role is unchanged and stays on
/// <c>Permissions.Users.*</c> - it is an operation on the user, not on the role. A guard that also
/// blocked assignment would satisfy every negative assertion while removing the operation a tenant
/// administrator actually needs, which is the failure mode of every permission guard: over-refusal.
/// </para>
/// <para>
/// <b>Roles stay installation-wide.</b> Pass 32 §4.3 measured the alternative: <c>ApplicationRole</c>
/// gaining a tenant means replacing Identity's own <c>RoleNameIndex</c> and every
/// <c>FindByNameAsync</c>/<c>RoleExistsAsync</c> lookup, which have no tenant term and live inside
/// the framework. This is a pure AUTHORIZATION change - no column, no migration, no seeding change,
/// and no cache-partition consequence, which is why <c>RoleDataSourceService.Scope</c> stays
/// <c>Global</c>.
/// </para>
/// <para>
/// <b>Every call site checks, because there is no chokepoint.</b> Role administration bypasses
/// Mediator - the pages hold a <c>RoleManager&lt;ApplicationRole&gt;</c> and call it directly - so
/// <c>AuthorizationBehaviour</c>'s deny-by-default never runs. That is the same situation
/// <c>AdministratorProtectionService</c> is in, and the guards below go where its guards already
/// are rather than into a second location. The six write paths are:
/// </para>
/// <list type="bullet">
/// <item><description><c>RoleFormDialog.Submit</c> - create</description></item>
/// <item><description><c>RoleFormDialog.Submit</c> - rename and re-describe</description></item>
/// <item><description><c>Roles.OnDelete</c> - single</description></item>
/// <item><description><c>Roles.OnDeleteChecked</c> - bulk</description></item>
/// <item><description><c>Roles.ProcessImportedRolesAsync</c> - import</description></item>
/// <item><description><c>PermissionAssignmentService.AssignRoleAsync</c> / <c>AssignRoleBulkAsync</c></description></item>
/// </list>
/// <para>
/// <b>This is a different guarantee from <c>AdministratorProtectionService</c>'s and must not be
/// conflated with it.</b> Those rules keep the INSTALLATION administrable - the Admin role must
/// survive, keep its grant and keep a member - and they bind the holder of this right as much as
/// anyone. This rule decides who may touch role definitions at all. Both run, in that order, and
/// neither substitutes for the other.
/// </para>
/// <para>
/// <b><see cref="IPermissionQueryService"/>, not <c>IPermissionService</c>.</b> The latter resolves
/// the principal through Blazor's <c>AuthenticationStateProvider</c>, so a non-Blazor host cannot
/// construct anything depending on it - Pass 27 and Pass 28 both hit that, and
/// <c>PermissionAssignmentService</c> is reachable outside a circuit. This is an Application-layer
/// type, so it must stay host-neutral.
/// </para>
/// <para>
/// <b>The refusal is an exception, not a <c>Result</c> failure.</b> That is the opposite of the
/// choice <c>SharedPicklistWrite</c> made, and for a stated reason: those callers are Mediator
/// handlers that already return <c>Result&lt;T&gt;</c>, whereas these are a dialog, a page and a
/// service whose every existing refusal - <c>AdministratorProtectionService</c>'s three and
/// <c>PermissionAssignmentService</c>'s grant-what-you-hold - is a
/// <see cref="ForbiddenAccessException"/> the caller catches and surfaces in a snackbar. A second
/// refusal shape on the same buttons would be the inconsistency, not the consistency.
/// </para>
/// </remarks>
public static class RoleDefinitionWrite
{
    /// <summary>
    /// The refusal a caller without the right receives, for any of create, rename, delete,
    /// re-permission or import.
    /// </summary>
    /// <remarks>
    /// <b>One message for every refusal, deliberately</b>, and it names WHY rather than only what:
    /// roles are shared by every tenant, so the refusal is about the blast radius of the change and
    /// not about this particular role. It also says what the caller CAN still do, because the
    /// commonest reading of "you may not change this role" is "you may not use roles at all".
    /// </remarks>
    public const string Refused =
        "Roles are shared by every tenant in this installation, so defining them - creating, " +
        "renaming, deleting, re-permissioning or importing - requires the 'manage role " +
        "definitions' permission. Assigning users to existing roles does not.";

    /// <summary>
    /// Whether <paramref name="userId"/> holds <c>Permissions.Roles.ManageDefinitions</c>.
    /// </summary>
    /// <remarks>
    /// <b>Fails closed on every path that is not an affirmative grant</b> - no user id, an unknown
    /// user, a permission query returning nothing. There is no branch in which an error permits the
    /// write, because permitting is a single explicit result reached only by holding the right.
    /// </remarks>
    public static async Task<bool> MayDefineRolesAsync(
        IPermissionQueryService permissions,
        string? userId)
    {
        if (string.IsNullOrEmpty(userId)) return false;

        var held = await permissions.GetAllPermissionsByUserId(userId);

        return held.Any(p =>
            p.Assigned && string.Equals(
                p.ClaimValue, Permissions.Roles.ManageDefinitions, StringComparison.Ordinal));
    }

    /// <summary>
    /// Throws <see cref="ForbiddenAccessException"/> unless <paramref name="userId"/> may define
    /// roles. Called at every role-definition write path before anything is written.
    /// </summary>
    /// <remarks>
    /// Checked BEFORE the write and, where a confirmation prompt exists, before the prompt - so a
    /// refused caller is told immediately rather than after agreeing to a deletion that cannot
    /// happen. That is the placement <c>AdministratorProtectionService.EnsureRoleCanBeDeleted</c>
    /// already has in <c>Roles.OnDelete</c>.
    /// </remarks>
    public static async Task EnsureAllowedAsync(
        IPermissionQueryService permissions,
        string? userId)
    {
        if (!await MayDefineRolesAsync(permissions, userId))
        {
            throw new ForbiddenAccessException(Refused);
        }
    }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Application.Common.Security;

namespace CleanArchitecture.Blazor.Application.Features.PicklistSets;

/// <summary>
/// Who may write the SHARED picklist rows - the ones with no tenant, that every tenant sees.
/// </summary>
/// <remarks>
/// <para>
/// <b>One definition, three consumers</b> - the add/edit command, the delete command and the admin
/// grid. Pass 28's precedent applies and the stake is the same as Pass 29's: two copies of this rule
/// would not disagree about which rows to touch, they would disagree about <b>whether to check at
/// all</b>, and the copy that forgot would be the one reached by whichever caller was written second.
/// </para>
/// <para>
/// <b>The guard belongs in the handlers, not the page.</b> Both commands go through Mediator and are
/// reachable by any caller, so a rule enforced only by what the grid renders is not a rule. The grid
/// reads the same right to decide what to offer, which is a second line rather than the boundary.
/// </para>
/// <para>
/// <b>This is a WRITE right over data nothing hides.</b> A shared row is visible to every tenant by
/// design - Pass 31's filter admits <c>TenantId == null</c> deliberately - so nothing here widens a
/// disclosure. It decides who may change a value every tenant depends on. Pass 31 §C's refusal of a
/// cross-tenant READ escape is untouched: no path below grants sight of another tenant's private
/// rows, and the query filter the caller cannot drop keeps them invisible.
/// </para>
/// <para>
/// <b><see cref="IPermissionQueryService"/>, not <c>IPermissionService</c>.</b> The latter resolves
/// the principal through Blazor's <c>AuthenticationStateProvider</c>, so a non-Blazor host cannot
/// construct anything depending on it - Pass 27 and Pass 28 both hit that. This is an
/// Application-layer type read by handlers, so it must stay host-neutral.
/// </para>
/// </remarks>
public static class SharedPicklistWrite
{
    /// <summary>
    /// The refusal a caller without the right receives, for any of create, edit or delete.
    /// </summary>
    /// <remarks>
    /// <b>One message for every refusal, deliberately.</b> It follows the posture
    /// <c>TenantSwitchService.SwitchToTenantAsync</c> established: a <c>Result</c> failure carrying a
    /// stated reason, returned from the handler on the arguments actually used - not an exception,
    /// and not a silent no-op that reports success while changing nothing.
    /// <para>
    /// It names the shared partition rather than the row, because a refusal that distinguished
    /// "this row is shared" from "this row is another tenant's" would be a disclosure. In practice
    /// the second case cannot arise - the query filter means another tenant's row is simply not
    /// found - but the message should not depend on that staying true.
    /// </para>
    /// </remarks>
    public const string Refused =
        "This picklist value is shared with every tenant. Changing it requires the " +
        "'manage shared picklists' permission.";

    /// <summary>
    /// Whether a row belongs to the installation rather than to a tenant.
    /// </summary>
    /// <remarks>
    /// <c>IsNullOrEmpty</c> rather than <c>is null</c>: the column is nullable and nothing writes an
    /// empty string today, but an empty tenant id would be neither a real tenant nor a shared row,
    /// and treating it as private would leave it editable by everyone and visible to nobody.
    /// </remarks>
    public static bool IsShared(string? tenantId) => string.IsNullOrEmpty(tenantId);

    /// <summary>
    /// Whether <paramref name="userId"/> holds <c>Permissions.PicklistSets.ManageShared</c>.
    /// </summary>
    /// <remarks>
    /// <b>Fails closed on every path that is not an affirmative grant</b> - no user id, an unknown
    /// user, a permission query returning nothing. There is no branch in which an error permits the
    /// write, because permitting is a single explicit result reached only by holding the right.
    /// </remarks>
    public static async Task<bool> MayManageSharedAsync(
        IPermissionQueryService permissions,
        string? userId)
    {
        if (string.IsNullOrEmpty(userId)) return false;

        var held = await permissions.GetAllPermissionsByUserId(userId);

        return held.Any(p =>
            p.Assigned && string.Equals(
                p.ClaimValue, Permissions.PicklistSets.ManageShared, StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether this write may proceed, given the tenants of the rows it would affect.
    /// </summary>
    /// <param name="affectedTenantIds">
    /// The <c>TenantId</c> of every row the write touches. For a create, the tenant the new row
    /// WOULD be stamped with - which is the ambient principal's, and null for a tenantless one.
    /// </param>
    /// <remarks>
    /// <b>The permission query is skipped when no shared row is involved</b>, which is the common
    /// case and keeps a tenant administrator editing their own rows off the permission path
    /// entirely. It is a short-circuit on the cheap side: skipping means allowing, and it is reached
    /// only when nothing shared is being touched.
    /// </remarks>
    public static async Task<bool> IsAllowedAsync(
        IEnumerable<string?> affectedTenantIds,
        IPermissionQueryService permissions,
        string? userId)
    {
        if (!affectedTenantIds.Any(IsShared)) return true;

        return await MayManageSharedAsync(permissions, userId);
    }
}

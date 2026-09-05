// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Application.Common.Security;

namespace CleanArchitecture.Blazor.Application.Features.SecuritySettings;

/// <summary>
/// Who may write the security policy - which is one row, in force for every tenant at once.
/// </summary>
/// <remarks>
/// <para>
/// <b>One definition, two consumers</b> - the update handler and the settings screen. The stake is
/// Pass 29's: two copies of this rule would not disagree about which row to touch, they would
/// disagree about <b>whether to check at all</b>, and the copy that forgot would be the one reached
/// by whichever caller was written second.
/// </para>
/// <para>
/// <b>The guard belongs in the handler, not the page.</b> <c>UpdateSecurityPolicyCommand</c> goes
/// through Mediator and is reachable by any caller, so a rule enforced only by what the screen
/// renders is not a rule. The screen reads the same right to decide whether to offer a form or a
/// read-only view, which is a second line rather than the boundary.
/// </para>
/// <para>
/// <b>Unlike the picklist case there is no partition to test.</b> <c>SharedPicklistWrite</c> asks
/// "is this row shared?" and skips the permission query when it is not; here every row is shared, so
/// the question is only whether the caller holds the right. That is why this type is so much
/// smaller - and why there is no short-circuit to get wrong.
/// </para>
/// <para>
/// <b><see cref="IPermissionQueryService"/>, not <c>IPermissionService</c>.</b> The latter resolves
/// the principal through Blazor's <c>AuthenticationStateProvider</c>, so a non-Blazor host cannot
/// construct anything depending on it - Pass 27 and Pass 28 both hit that. This is an
/// Application-layer type read by a handler, so it must stay host-neutral.
/// </para>
/// </remarks>
public static class InstallationPolicyWrite
{
    /// <summary>
    /// The refusal a caller without the right receives.
    /// </summary>
    /// <remarks>
    /// A <c>Result</c> failure carrying a stated reason, which is the posture
    /// <c>AddEditPicklistSetCommandHandler</c> and <c>TenantSwitchService.SwitchToTenantAsync</c>
    /// established for a forbidden write behind Mediator - not an exception, and not a silent no-op
    /// that reports success while changing nothing. (<c>RoleDefinitionWrite</c> throws instead
    /// because its callers are pages and a service with no <c>Result</c> to return; this one has
    /// one.)
    /// <para>
    /// It names the SCOPE rather than the screen, because the surprising part is not that the save
    /// was refused but that the value reaches every tenant.
    /// </para>
    /// </remarks>
    public const string Refused =
        "The session policy is installation-wide: it applies to every tenant at once. Changing it " +
        "requires the 'manage installation policy' permission.";

    /// <summary>
    /// Whether <paramref name="userId"/> holds
    /// <c>Permissions.SecuritySettings.ManageInstallationPolicy</c>.
    /// </summary>
    /// <remarks>
    /// <b>Fails closed on every path that is not an affirmative grant</b> - no user id, an unknown
    /// user, a permission query returning nothing. There is no branch in which an error permits the
    /// write, because permitting is a single explicit result reached only by holding the right.
    /// </remarks>
    public static async Task<bool> IsAllowedAsync(
        IPermissionQueryService permissions,
        string? userId)
    {
        if (string.IsNullOrEmpty(userId)) return false;

        var held = await permissions.GetAllPermissionsByUserId(userId);

        return held.Any(p =>
            p.Assigned && string.Equals(
                p.ClaimValue,
                Permissions.SecuritySettings.ManageInstallationPolicy,
                StringComparison.Ordinal));
    }
}

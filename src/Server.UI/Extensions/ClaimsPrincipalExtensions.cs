using System.Security.Claims;

namespace CleanArchitecture.Blazor.Server.UI.Extensions;

/// <summary>
/// The two claims this application actually reads off a principal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Twelve of the fourteen methods here were deleted in Pass 40, having no callers.</b> They were
/// a claims-reading surface built ahead of a need that never arrived: email, phone number, provider,
/// display name, profile picture, superior name and id, tenant name, tenant id, status, assigned
/// roles, and roles. A method nobody calls is not free - it invites the next reader to call it, and
/// in one case that had already cost a cross-tenant read escape.
/// </para>
/// <para>
/// <b><c>GetTenantId</c> is the one worth naming, because deleting it was the point.</b> The tenant
/// CLAIM is written by exactly one place - <c>TenantSwitchService.RefreshUserClaimsAsync</c>,
/// reachable only from <c>SwitchToTenantAsync</c> - and <c>ApplicationUserClaimsPrincipalFactory</c>
/// does not add one. So a user who has never switched tenant carries no claim at all: Pass 36
/// measured <c>AspNetUserClaims</c> as empty in a freshly seeded installation while the
/// administrator carried a real tenant on their user row. Until Pass 38, <c>FileEndpoints</c> read
/// the tenant from that claim, got null for essentially every user, and passed an empty string into
/// <c>VisibleDocumentSpecification</c> - whose no-tenant branch drops the tenant clause - so the file
/// endpoint served every public document in the installation. Pass 38 fixed the call site and left a
/// warning here; a method that cannot be called correctly should not be callable, so it is gone.
/// </para>
/// <para>
/// <b>Where to get a tenant instead.</b> Inside a Blazor circuit or a Mediator handler, the ambient
/// <c>IUserContextAccessor.Current.TenantId</c>, pushed by <c>UserContextHubFilter</c>. On an HTTP
/// path, where the ambient context is null, <c>IUserContextLoader.LoadAsync</c>: it takes a
/// <see cref="ClaimsPrincipal"/>, reads the tenant from the USER ROW, caches per user, and is
/// already invalidated on tenant switch.
/// </para>
/// <para>
/// The two that remain read framework claims that Identity always issues, so neither has the
/// absent-for-most-users problem that the tenant claim has.
/// </para>
/// </remarks>
public static class ClaimsPrincipalExtensions
{
    public static string? GetUserId(this ClaimsPrincipal claimsPrincipal)
    {
        return claimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier);
    }

    public static string? GetUserName(this ClaimsPrincipal claimsPrincipal)
    {
        return claimsPrincipal.FindFirstValue(ClaimTypes.Name);
    }
}

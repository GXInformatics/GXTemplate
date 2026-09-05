using System.Security.Claims;
using CleanArchitecture.Blazor.Application.Common.Constants;

namespace CleanArchitecture.Blazor.Server.UI.Extensions;

public static class ClaimsPrincipalExtensions
{
    public static string? GetEmail(this ClaimsPrincipal claimsPrincipal)
    {
        return claimsPrincipal.FindFirstValue(ClaimTypes.Email);
    }

    public static string? GetPhoneNumber(this ClaimsPrincipal claimsPrincipal)
    {
        return claimsPrincipal.FindFirstValue(ClaimTypes.MobilePhone);
    }

    public static string? GetUserId(this ClaimsPrincipal claimsPrincipal)
    {
        return claimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier);
    }

    public static string? GetUserName(this ClaimsPrincipal claimsPrincipal)
    {
        return claimsPrincipal.FindFirstValue(ClaimTypes.Name);
    }

    public static string? GetProvider(this ClaimsPrincipal claimsPrincipal)
    {
        return claimsPrincipal.FindFirstValue(ApplicationClaimTypes.Provider);
    }

    public static string? GetDisplayName(this ClaimsPrincipal claimsPrincipal)
    {
        return claimsPrincipal.FindFirstValue(ClaimTypes.GivenName);
    }

    public static string? GetProfilePictureDataUrl(this ClaimsPrincipal claimsPrincipal)
    {
        return claimsPrincipal.FindFirstValue(ApplicationClaimTypes.ProfilePictureDataUrl);
    }

    public static string? GetSuperiorName(this ClaimsPrincipal claimsPrincipal)
    {
        return claimsPrincipal.FindFirstValue(ApplicationClaimTypes.SuperiorName);
    }

    public static string? GetSuperiorId(this ClaimsPrincipal claimsPrincipal)
    {
        return claimsPrincipal.FindFirstValue(ApplicationClaimTypes.SuperiorId);
    }

    /// <inheritdoc cref="GetTenantId"/>
    public static string? GetTenantName(this ClaimsPrincipal claimsPrincipal)
    {
        return claimsPrincipal.FindFirstValue(ApplicationClaimTypes.TenantName);
    }

    /// <summary>
    /// The tenant claim - which is <b>absent for most users and stale for the rest</b>. Do not make
    /// a security decision on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The only writer of <c>ApplicationClaimTypes.TenantId</c> is
    /// <c>TenantSwitchService.RefreshUserClaimsAsync</c>, reachable only from
    /// <c>SwitchToTenantAsync</c>.</b> <c>ApplicationUserClaimsPrincipalFactory</c> does not add one.
    /// So a user who has never switched tenant carries NO claim at all - Pass 36 measured
    /// <c>AspNetUserClaims</c> as empty in a freshly seeded installation while the administrator
    /// carried a real tenant on their user row - and a user who has switched carries whatever was
    /// true when their claims were last written.
    /// </para>
    /// <para>
    /// <b>This has already cost one cross-tenant read escape.</b> Until Pass 38, <c>FileEndpoints</c>
    /// read the tenant here, got null for essentially every user, and passed an empty string into
    /// <c>VisibleDocumentSpecification</c> - whose no-tenant branch drops the tenant clause - so the
    /// file endpoint served every public document in the installation. <c>HubUserContext</c>'s
    /// remarks had recorded the same hazard for hubs; the warning simply was not where anyone
    /// reaching for a tenant would find it. It is here now.
    /// </para>
    /// <para>
    /// <b>Where to get a tenant instead.</b> Inside a Blazor circuit or a Mediator handler, the
    /// ambient <c>IUserContextAccessor.Current.TenantId</c> - pushed by <c>UserContextHubFilter</c>.
    /// On an HTTP path, where the ambient context is null, <c>IUserContextLoader.LoadAsync</c>:
    /// it takes this same principal, reads the tenant from the USER ROW, caches per user, and is
    /// already invalidated on tenant switch.
    /// </para>
    /// <para>
    /// <b>These two methods have no callers</b> as of Pass 38, and five of their siblings in this
    /// class have none either - see the report's §C for why deleting the dead surface was
    /// recommended but not smuggled into a security fix.
    /// </para>
    /// </remarks>
    public static string? GetTenantId(this ClaimsPrincipal claimsPrincipal)
    {
        return claimsPrincipal.FindFirstValue(ApplicationClaimTypes.TenantId);
    }

    public static bool GetStatus(this ClaimsPrincipal claimsPrincipal)
    {
        return Convert.ToBoolean(claimsPrincipal.FindFirstValue(ApplicationClaimTypes.Status));
    }

    public static string? GetAssignRoles(this ClaimsPrincipal claimsPrincipal)
    {
        return claimsPrincipal.FindFirstValue(ApplicationClaimTypes.AssignedRoles);
    }

    public static string[] GetRoles(this ClaimsPrincipal claimsPrincipal)
    {
        return claimsPrincipal.Claims.Where(x => x.Type == ClaimTypes.Role).Select(x => x.Value).ToArray();
    }
}

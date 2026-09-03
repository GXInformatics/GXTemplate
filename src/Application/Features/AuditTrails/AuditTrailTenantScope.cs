// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Application.Common.Constants;

namespace CleanArchitecture.Blazor.Application.Features.AuditTrails;

/// <summary>
/// The single exemption from the audit trail's global tenant filter.
/// </summary>
/// <remarks>
/// <b>The default is scoped, and this is the only way out of it.</b> Pass 29 put a named query
/// filter on <see cref="AuditTrail"/> in <c>ApplicationDbContext</c>, which inverts the default: a
/// new query is tenant-scoped whether or not its author thought about it, and every legitimate
/// cross-tenant read has to say so out loud. This method is where "out loud" is written down.
/// <para>
/// <b>One definition, two consumers</b> - the audit grid and the audit export. Pass 28 is the
/// precedent for extracting rather than repeating: this template has met the same defect three
/// times, and each time the second copy of a security rule was the one that was wrong. Here the
/// stake is higher than usual, because the two copies would not disagree about which ROWS to
/// return - they would disagree about whether to <b>drop the filter at all</b>.
/// </para>
/// <para>
/// <b>The right is checked HERE, not inside the filter.</b> It cannot be a term in the predicate:
/// <c>UserContext</c> carries the tenant, the allowed tenants and the roles, but no permissions, and
/// a query filter expression cannot perform the permission query it would need. So the shape is the
/// one Pass 27 established - resolve the right once, then exempt explicitly.
/// </para>
/// <para>
/// <b><see cref="IPermissionQueryService"/>, not <c>IPermissionService</c>.</b> The latter resolves
/// the principal through Blazor's <c>AuthenticationStateProvider</c>, so a non-Blazor host cannot
/// construct anything depending on it. Pass 27 hit exactly that when a datasource took the
/// dependency and <c>Application.IntegrationTests</c> stopped being able to build one, and Pass 28
/// hit it again in <c>TenantSwitchService</c>. This is an Application-layer type read by handlers,
/// so it must stay host-neutral.
/// </para>
/// </remarks>
public static class AuditTrailTenantScope
{
    /// <summary>
    /// The audit rows <paramref name="userId"/> may read: their own tenant's, or every tenant's if
    /// they hold <c>Permissions.AuditTrails.ViewAllTenants</c>.
    /// </summary>
    /// <remarks>
    /// <b>Fails closed by doing nothing.</b> Every path that is not an affirmative grant - no user
    /// id, an unknown user, a permission query that returns nothing - leaves the source untouched
    /// and therefore still filtered. There is no branch in which an error widens what is returned,
    /// because the widening is a single explicit call reached only by holding the right.
    /// </remarks>
    public static async Task<IQueryable<AuditTrail>> VisibleAsync(
        IQueryable<AuditTrail> source,
        IPermissionQueryService permissions,
        string? userId)
    {
        if (string.IsNullOrEmpty(userId)) return source;

        var held = await permissions.GetAllPermissionsByUserId(userId);
        var mayCrossTenants = held.Any(p =>
            p.Assigned && string.Equals(
                p.ClaimValue, Permissions.AuditTrails.ViewAllTenants, StringComparison.Ordinal));

        if (!mayCrossTenants) return source;

        // EXEMPTION, and the reason it is allowed: this principal holds
        // Permissions.AuditTrails.ViewAllTenants, an administrator right whose entire purpose is
        // reading audit history across tenants - the auditor and support-engineer case.
        //
        // By NAME, and the name matters twice over. It drops only the tenant filter, so soft-delete
        // (and anything else added later) keeps applying - the bare IgnoreQueryFilters() would drop
        // every filter on the entity and quietly widen far more than intended. And it is a constant
        // rather than a literal, so it cannot drift from the name the filter was registered under;
        // a drifted name does not throw, it silently drops nothing and leaves the holder wondering
        // why their right does nothing.
        return source.IgnoreQueryFilters([QueryFilters.Tenant]);
    }
}

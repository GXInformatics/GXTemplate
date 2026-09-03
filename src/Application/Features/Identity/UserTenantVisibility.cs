// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq.Expressions;
using CleanArchitecture.Blazor.Domain.Identity;

namespace CleanArchitecture.Blazor.Application.Features.Identity;

/// <summary>
/// Which users a principal may see: those in the tenants they are allowed to see, unless they hold
/// the cross-tenant right.
/// </summary>
/// <remarks>
/// <b>One definition, three consumers.</b> The users grid, the user export and
/// <c>UserDataSourceService</c> - which backs the "superior" picker - all bound their rows by this.
/// Pass 27 put the rule inline in <c>Users.razor</c>, where the grid and the export already shared
/// it; Pass 28 added a third consumer in a different layer and extracted it rather than writing the
/// clause a second time.
/// <para>
/// That is not tidiness. This template has met the same defect three times - the Documents
/// visibility rule spelled out per list view, the sink column sets, the tenant clause on the user
/// dialog - and each time the second copy was the one that was wrong. <c>VisibleDocumentSpecification</c>
/// is the precedent: "a security rule with two copies is a security rule with one copy that is out
/// of date."
/// </para>
/// <para>
/// <b>Fail closed, and the shape of the parameters enforces it.</b> There is no overload that omits
/// the visible set, and a null or empty set matches nothing - so a caller that cannot answer "which
/// tenants?" gets no rows rather than all of them. The three ways that happens are: no ambient
/// principal, a principal belonging to no tenant, and a user row whose own TenantId is null.
/// </para>
/// </remarks>
public static class UserTenantVisibility
{
    /// <summary>
    /// The users visible to a principal who may see <paramref name="visibleTenantIds"/>.
    /// </summary>
    /// <param name="viewAllTenants">
    /// Whether the principal holds <c>Permissions.Users.ViewAllTenants</c>. When true the bound does
    /// not apply and every user is visible, including users belonging to no tenant at all.
    /// </param>
    /// <param name="visibleTenantIds">
    /// The tenants the principal may see - <c>UserContext.AllowedTenantIds</c>, which is the union of
    /// their membership rows and their own current tenant. Null and empty both mean "none".
    /// </param>
    public static Expression<Func<ApplicationUser, bool>> IsVisibleTo(
        bool viewAllTenants,
        IReadOnlyCollection<string>? visibleTenantIds)
    {
        if (viewAllTenants)
        {
            return _ => true;
        }

        // Materialised as an array so the expression carries a value EF can translate to an IN
        // clause rather than a closure over a collection it cannot see into.
        var allowed = visibleTenantIds?.ToArray() ?? Array.Empty<string>();

        // A user with a null TenantId matches no id and is therefore invisible to everyone except a
        // cross-tenant holder. That falls out of failing closed rather than being a separate rule,
        // and it is asserted so it cannot change silently.
        return user => allowed.Contains(user.TenantId!);
    }
}

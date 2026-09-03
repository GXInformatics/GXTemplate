// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.Constants;

/// <summary>
/// The names of the global query filters configured on <c>ApplicationDbContext</c>.
/// </summary>
/// <remarks>
/// <b>In Application rather than Infrastructure, because both ends need them.</b> The filters are
/// registered in Infrastructure, but the call sites that exempt themselves from one are handlers in
/// Application - and Application must not reference Infrastructure. Putting the names here is what
/// lets both refer to the same constant instead of two string literals that agree only by habit.
/// <para>
/// <b>A drifted name does not throw.</b> <c>IgnoreQueryFilters(["Tenat"])</c> is not an error: EF
/// drops the filters that match and silently leaves the rest applying, so a typo produces a query
/// that is still filtered and a permission that appears to do nothing. That failure is invisible in
/// exactly the way this template's programme keeps finding costly, which is why these are constants
/// and never literals.
/// </para>
/// </remarks>
public static class QueryFilters
{
    /// <summary>
    /// Restricts an entity to the ambient tenant. Registered on <c>AuditTrail</c> since Pass 29.
    /// </summary>
    /// <remarks>
    /// Exempted only by <c>AuditTrailTenantScope.VisibleAsync</c>, which checks
    /// <c>Permissions.AuditTrails.ViewAllTenants</c> first.
    /// </remarks>
    public const string Tenant = "Tenant";

    /// <summary>
    /// Hides soft-deleted rows. Registered for <c>ISoftDelete</c>, which currently has no
    /// implementors - see <c>ModelBuilderExtensions.ApplyGlobalFilters</c>.
    /// </summary>
    public const string SoftDelete = "SoftDelete";
}

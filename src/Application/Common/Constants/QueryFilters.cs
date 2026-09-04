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
    /// Restricts an entity to the ambient tenant. Registered on <c>AuditTrail</c> since Pass 29 and
    /// on <c>PicklistSet</c> since Pass 31.
    /// </summary>
    /// <remarks>
    /// <b>One name, two predicates, because a null tenant means opposite things for the two
    /// entities.</b> An audit row with no tenant is an installation-level EVENT belonging to nobody,
    /// so its filter is strict equality. A picklist row with no tenant is SHARED REFERENCE DATA
    /// belonging to everyone, so its filter is <c>TenantId == null || TenantId == current</c>. The
    /// predicates live beside their entities in <c>ApplicationDbContext.OnModelCreating</c>; this
    /// name is what both are registered under.
    /// <para>
    /// The name is shared on purpose rather than split in two: it is what an exemption names, and an
    /// exemption means the same thing for either entity - "read across tenants, having checked a
    /// right". Today the only one is <c>AuditTrailTenantScope.VisibleAsync</c>, which checks
    /// <c>Permissions.AuditTrails.ViewAllTenants</c> first. <b>Picklists have no exemption and no
    /// cross-tenant right</b>, by decision rather than omission - see the README's Tenancy section.
    /// </para>
    /// </remarks>
    public const string Tenant = "Tenant";

    /// <summary>
    /// Hides soft-deleted rows. Registered for <c>ISoftDelete</c>, which currently has no
    /// implementors - see <c>ModelBuilderExtensions.ApplyGlobalFilters</c>.
    /// </summary>
    public const string SoftDelete = "SoftDelete";
}

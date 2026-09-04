// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using CleanArchitecture.Blazor.Domain.Common.Entities;

namespace CleanArchitecture.Blazor.Domain.Entities;

/// <summary>
/// A named set of reference values - Status, Unit, Brand as shipped.
/// </summary>
/// <remarks>
/// <b>Shared reference data with per-tenant additions, since Pass 31.</b> A row with a null
/// <see cref="TenantId"/> is installation-wide and visible to every tenant; a row carrying a tenant
/// belongs to that tenant alone. The rule is a named global query filter on
/// <c>ApplicationDbContext</c> - <c>TenantId == null || TenantId == current</c> - so a query over
/// this entity is scoped whether or not its author thought about it. Pass 24 stamped the entity
/// through <see cref="IMayHaveTenant"/> and deliberately left the decision open; Pass 31 closed it.
/// <para>
/// <b>Two consequences worth knowing before you extend this.</b> First, a row created while a
/// principal is signed in is stamped with THAT principal's tenant and is therefore private to it -
/// the shared rows are the ones written with no ambient principal, which today means seeding.
/// Second, the import's duplicate check is now per-tenant plus shared, which is what it should
/// always have been: two tenants may import the same picklist name without colliding, and neither
/// may shadow a value the installation already ships.
/// </para>
/// <para>
/// The marker was chosen over a bare property after checking what else keys off it. Nothing did:
/// its only consumers are the two lines in <c>AuditableEntityInterceptor.SetCreationAuditInfo</c>
/// that stamp on insert. The filter is registered by an EXPLICIT ENTITY LIST rather than off this
/// marker - see <c>ApplicationDbContext.OnModelCreating</c> for why the interface is the wrong key.
/// </para>
/// </remarks>
public class PicklistSet : BaseAuditableEntity, IMayHaveTenant, IAuditable
{
    public Picklist Name { get; set; } = Picklist.Brand;
    public string? Value { get; set; }
    public string? Text { get; set; }
    public string? Description { get; set; }

    /// <inheritdoc cref="PicklistSet" />
    public string? TenantId { get; set; }
}

public enum Picklist
{
    [Display(Name = "Status")] Status,
    [Display(Name = "Unit")] Unit,
    [Display(Name = "Brand")] Brand
}

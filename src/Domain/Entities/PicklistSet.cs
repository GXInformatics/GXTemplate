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
/// <b>Tenant-stamped, not tenant-scoped.</b> <see cref="IMayHaveTenant"/> is implemented so that
/// <c>AuditableEntityInterceptor</c> records which tenant created each row. Nothing filters on it:
/// picklists are still shared reference data visible to everyone, and whether they should stay that
/// way is an open product question. The column is added now only because the business schema
/// regenerates its InitialCreate, so it costs a regeneration today and a data migration later.
/// <para>
/// The marker was chosen over a bare property after checking what else keys off it. Nothing does:
/// its only consumers are the two lines in <c>AuditableEntityInterceptor.SetCreationAuditInfo</c>
/// that stamp on insert. So implementing it buys the stamp and changes nothing else - the global
/// filter helper keys off <c>ISoftDelete</c>, not this.
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

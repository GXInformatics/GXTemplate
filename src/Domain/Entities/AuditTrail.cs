// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace CleanArchitecture.Blazor.Domain.Entities;

public class AuditTrail : IEntity<int>
{
    public int Id { get; set; }
    public string? UserId { get; set; }
    public virtual ApplicationUser? Owner { get; set; }

    /// <summary>
    /// The tenant the audited change was made in, captured at write time.
    /// </summary>
    /// <remarks>
    /// <b>It has to be stored, not derived.</b> The obvious alternative - reach the tenant later by
    /// joining <see cref="UserId"/> through to <c>ApplicationUser.TenantId</c> - is wrong, and
    /// wrong in the direction that matters: <c>TenantSwitchService.SwitchToTenantAsync</c> writes
    /// <c>user.TenantId</c> in place, so the user row records the tenant somebody is in NOW, not
    /// the one they were in when the change happened. A join would therefore re-attribute every
    /// historical row the moment a user switched tenants, which is the one thing an audit trail
    /// must never do.
    /// <para>
    /// <b>Nullable, and null is a real value.</b> Seeding, startup provisioning and any future
    /// background work save with no ambient user context, so those rows genuinely belong to the
    /// installation rather than to a tenant. Inventing a tenant for them would be a worse record
    /// than admitting they have none.
    /// </para>
    /// <para>
    /// <b>Written and not yet read.</b> Nothing filters on this column today - the audit trail is
    /// still installation-wide. The column exists now because the business schema regenerates its
    /// InitialCreate, so adding it costs a regeneration today and a data migration once a customer
    /// is deployed.
    /// </para>
    /// </remarks>
    public string? TenantId { get; set; }

    public AuditType AuditType { get; set; }
    public string? TableName { get; set; }
    public DateTime DateTime { get; set; }
    public Dictionary<string, AuditChange>? Changes { get; set; }
    public List<string>? AffectedColumns { get; set; }
    public Dictionary<string, string> PrimaryKey { get; set; } = new();
    public List<PropertyEntry> TemporaryProperties { get; } = new();
    public bool HasTemporaryProperties => TemporaryProperties.Any();
}

public enum AuditType
{
    None,
    Create,
    Update,
    Delete
}
public class AuditChange
{
    public string? Old { get; set; }
    public string? New { get; set; }
}

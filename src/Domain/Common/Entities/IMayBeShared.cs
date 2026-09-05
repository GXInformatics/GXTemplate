// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Domain.Common.Entities;

/// <summary>
/// An <see cref="IMayHaveTenant"/> entity that has a legitimate INSTALLATION-WIDE partition, and can
/// therefore be created deliberately with no tenant.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a flag is needed at all.</b> <c>AuditableEntityInterceptor.SetCreationAuditInfo</c> stamps
/// an <see cref="IMayHaveTenant"/> row whose <c>TenantId</c> is null with the ambient principal's
/// tenant. Null is therefore the sentinel for "not set yet" - and it is ALSO the value that means
/// "shared". A tenant-scoped principal has no way to say which one they mean, which is why Pass 32
/// found that a <c>PicklistSets.ManageShared</c> holder could edit shared rows but never create one.
/// This interface is that distinction, and nothing more.
/// </para>
/// <para>
/// <b>Why this is not a general escape from stamping.</b> Pass 24 made stamping deliberate, and an
/// opt-out reachable from anywhere is the kind of hole that gets found two years later. Four things
/// contain it:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>It is opt-in by TYPE.</b> Only entities implementing this interface can be marked, and today
/// that is <c>PicklistSet</c> alone. <c>Document</c>, <c>AuditTrail</c> and every
/// <c>IMustHaveTenant</c> entity are structurally out of reach - there is no cast that gets there.
/// </description></item>
/// <item><description>
/// <b>It is per INSTANCE.</b> There is no ambient switch, no scope, no service to resolve. Marking
/// one row says nothing about the next, and nothing can turn stamping off for a save, a request or
/// a process.
/// </description></item>
/// <item><description>
/// <b>It is <c>[NotMapped]</c> on every implementer.</b> The flag never reaches a column, so it
/// cannot be set by a client, cannot survive a round-trip through a DTO, and cannot be true on an
/// entity read back from the database. It exists only between construction and <c>SaveChanges</c>.
/// </description></item>
/// <item><description>
/// <b>It grants nothing.</b> Setting it does not authorise anything: the handler must still pass
/// the right's guard - <c>SharedPicklistWrite.IsAllowedAsync</c> over the tenant the row WILL carry
/// - and it does so on the strength of this flag rather than in spite of it. A caller who sets the
/// flag without the right is refused before the entity is added.
/// </description></item>
/// </list>
/// <para>
/// <b>Creation only.</b> Nothing here moves an existing row between partitions. Doing so would
/// change which unique index constrains it and which tenants see it, and it is not a capability
/// anyone has asked for; the edit path ignores the flag entirely.
/// </para>
/// </remarks>
public interface IMayBeShared : IMayHaveTenant
{
    /// <summary>
    /// Set on a NEW entity to mean "the null tenant is deliberate - this row belongs to the
    /// installation". Transient and never persisted; see the remarks on the interface.
    /// </summary>
    bool CreateAsShared { get; set; }
}

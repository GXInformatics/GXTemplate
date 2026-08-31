// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Domain.Common.Entities;

namespace CleanArchitecture.Blazor.Domain.Entities;

/// <summary>
/// The security policy an administrator has set for this installation - today, the idle timeout.
/// </summary>
/// <remarks>
/// <b>One row.</b> The provider reads the first row and seeds one from configuration when the table
/// is empty, so a fresh database needs no seeding step of its own. Adding a tenant column later is a
/// migration plus a cache key, not a redesign - which is why the reader goes through
/// <c>IIdleTimeoutPolicyProvider</c> rather than querying the table at its call sites.
/// <para>
/// <b>Audited.</b> Changing how long a session may sit unattended is a security event, so the entity
/// carries <see cref="IAuditable"/> and its before/after values land in AuditTrails in the same
/// transaction as the change.
/// </para>
/// <para>
/// <b>A template table, not a business model.</b> It derives from <see cref="BaseAuditableEntity"/>
/// and is therefore an <see cref="IBusinessEntity"/> like anything a project writes - so, like
/// Documents and PicklistSets, its configuration names its table explicitly to keep it out of the
/// <c>core</c> schema. See <c>SecurityPolicyConfiguration</c>.
/// </para>
/// </remarks>
public class SecurityPolicy : BaseAuditableEntity, IAuditable
{
    /// <summary>Minutes a session may sit idle before the warning countdown opens.</summary>
    public int IdleTimeoutMinutes { get; set; }

    /// <summary>Seconds the warning counts down before the session ends.</summary>
    public int CountdownSeconds { get; set; }
}

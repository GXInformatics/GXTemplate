// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Domain.Common.Entities;

namespace CleanArchitecture.Blazor.Domain.Entities;

/// <summary>
/// The security policy an administrator has set for this installation - today, the idle timeout.
/// </summary>
/// <remarks>
/// <b>One row, and installation-wide deliberately.</b> The provider reads the first row and seeds
/// one from configuration when the table is empty, so a fresh database needs no seeding step of its
/// own. Every reader goes through <c>IIdleTimeoutPolicyProvider</c> rather than querying this table,
/// which is what keeps a future change to that decision confined.
/// <para>
/// <b>The reason it is installation-wide is the authentication cookie, not inertia.</b>
/// <c>IdleTimeoutSettings.CookieLifetime</c> derives from <c>MaxIdleTimeoutMinutes</c>, and the
/// cookie is issued at sign-in - before any tenant is known - and cannot be shortened afterwards. So
/// the outer bound is irreducibly installation-wide, and per-tenant rows could only ever let a
/// customer pick a point inside a band the operator has already fixed in configuration. Writing the
/// row requires <c>Permissions.SecuritySettings.ManageInstallationPolicy</c> since Pass 37, so the
/// cross-tenant reach is named and revocable rather than tacit.
/// </para>
/// <para>
/// <b>This remark used to say adding a tenant column later was "a migration plus a cache key, not a
/// redesign". That was measured in Pass 36 and it was optimistic</b>, by enough to have shaped a
/// brief. The real cost is a migration - which would be this template's FIRST second migration, on
/// all three provider chains, now enforced together by <c>ModelMatchesMigrationsTests</c> - plus a
/// signature change across roughly 22 call sites of the provider's two read methods, a tag-based
/// cache flush in place of a single key removal, a second permission, a UI concept the settings
/// screen does not have ("you are overriding the installation default"), and a cached database
/// dependency inside cookie validation. Not a redesign; not small either.
/// </para>
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

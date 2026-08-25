// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Domain.Common.Entities;

/// <summary>
/// Marks an entity whose every insert, update and delete is written to <c>AuditTrails</c> in the same
/// transaction as the change itself. Auditing is opt-in: adding this interface is the whole decision,
/// and an entity without it is deliberately not audited rather than accidentally missed.
/// <para>
/// The ASP.NET Identity entities (users, roles, claims, logins, tokens) are deliberately NOT marked.
/// Identity writes through its own managers, often several saves per logical operation, so marking
/// them would produce a high-volume, hard-to-read trail that still would not capture the operation a
/// reader actually cares about ("who granted this permission"). Auditing identity properly needs a
/// purpose-built trail at the operation level, which is deferred rather than approximated here.
/// </para>
/// <para>
/// Renamed from <c>IAuditTrial</c>, which was a typo for "trail" and named the artefact rather than
/// the property. This is a deliberate divergence from upstream CleanArchitecture.Blazor.
/// </para>
/// </summary>
public interface IAuditable
{
}

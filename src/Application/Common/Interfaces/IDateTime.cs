// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.Interfaces;

/// <summary>
/// The clock, as handlers should obtain it: injectable, and UTC.
/// </summary>
/// <remarks>
/// The member is <c>UtcNow</c> and not <c>Now</c>, and the rename in Pass 14B was the whole change.
/// The implementation always returned <c>DateTime.UtcNow</c>, so nothing was broken - but a member
/// called <c>Now</c> beside a <c>DateTime.Now</c> that means something else invites a contributor to
/// add the local-time one, and under PostgreSQL's <c>timestamptz</c> a <c>Kind=Local</c> DateTime is
/// not a subtly wrong value, it is a rejected bind. The name now says which clock it is.
/// <para>
/// Every persisted <c>DateTime</c> in this application is UTC. This interface is how a handler gets
/// one without reaching for a static, which is also what lets the audit tests pin an exact instant.
/// </para>
/// </remarks>
public interface IDateTime
{
    DateTime UtcNow { get; }
}

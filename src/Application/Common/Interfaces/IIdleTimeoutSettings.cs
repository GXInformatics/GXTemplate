// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.Interfaces;

/// <summary>
/// The deployment's idle-timeout <b>bounds</b>, and the values a fresh database is seeded with.
/// </summary>
/// <remarks>
/// This is deliberately not the effective policy. Configuration supplies only what an administrator
/// may not exceed; the policy in force is administered at runtime and read through
/// <see cref="IIdleTimeoutPolicyProvider"/>.
/// <para>
/// The split matters for one value in particular. <see cref="MaxIdleTimeoutMinutes"/> alone decides
/// the authentication cookie's absolute lifetime, which is fixed when the cookie is issued and
/// cannot be shortened retroactively - so it has to be a deployment decision, not an administrator
/// one. Every other bound exists to keep an administrator from configuring a policy the cookie
/// cannot honour.
/// </para>
/// </remarks>
public interface IIdleTimeoutSettings
{
    /// <summary>
    /// When false the feature is inert end to end: no JS module is fetched, no principal check runs,
    /// and neither settings screen is reachable.
    /// </summary>
    bool Enabled { get; set; }

    /// <summary>Idle window seeded into a fresh database, in minutes.</summary>
    int DefaultIdleTimeoutMinutes { get; set; }

    /// <summary>Warning countdown seeded into a fresh database, in seconds.</summary>
    int DefaultCountdownSeconds { get; set; }

    /// <summary>The shortest idle window any policy - administered or per-user - may specify.</summary>
    int MinIdleTimeoutMinutes { get; set; }

    /// <summary>
    /// The longest idle window any policy may specify, and the only value that sizes the
    /// authentication cookie.
    /// </summary>
    int MaxIdleTimeoutMinutes { get; set; }

    /// <summary>
    /// Whether a user may shorten their own idle window. Never lengthen it - see
    /// <see cref="IIdleTimeoutPolicyProvider"/>.
    /// </summary>
    bool AllowUserOverride { get; set; }

    /// <summary>
    /// Whether the browser pings the keep-alive endpoint while the user is active. Off makes the
    /// sliding cookie unable to renew inside a long-lived Blazor circuit; see the endpoint's remarks.
    /// </summary>
    bool KeepAlivePingEnabled { get; set; }

    /// <summary>
    /// Slack added to the cookie's lifetime on top of the maximum window and the countdown, so that
    /// the cookie never expires marginally before the enforcement that is meant to end the session.
    /// </summary>
    int CookieGraceMinutes { get; set; }
}
